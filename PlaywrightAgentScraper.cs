using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using PhoneNumbers;

namespace Advista.Tasks._180no.CrawlerLocators.Components.Anthropic.Services;

/// <summary>
/// Three-phase, tips-driven scraper:
///   1. Discovery   — enumerate real clickable elements (anchors + clickable &lt;li&gt;) WITH their
///                    href / text / classes, and let Claude pick the department links using the
///                    human <c>tips</c>. Real hrefs keep the selector↔data connection that a YAML
///                    accessibility snapshot would otherwise lose.
///   2. Extraction  — visit each department page, collect a focused payload (headings, tel:/mailto:
///                    links in DOM order, visible text) and let Claude extract one contact record
///                    following the extraction rules (top contact, ignore people lists / footer).
///   3. Filtering   — remove contact sets that repeat on &gt;80% of pages (footer / head office).
/// </summary>
public sealed class PlaywrightAgentScraper : IAgentScraper, IAsyncDisposable
{
    private readonly AnthropicClient _claude;
    private readonly AnthropicBrowserOptions _options;
    private readonly IOptions<ManagerOptions> _managerOptions;
    private readonly ILogger _logger;

    // Playwright lifetime — created lazily, shared across calls
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public PlaywrightAgentScraper(AnthropicClient claude, AnthropicBrowserOptions options, IOptions<ManagerOptions> managerOptions, ILogger logger)
    {
        _claude = claude;
        _options = options;
        _managerOptions = managerOptions;
        _logger = logger;
    }

    public async Task<List<DepartmentAnthAuto>> ExtractAsync(
        string url,
        string prompt,
        string? tips = null,
        string defaultCountry = "NO",
        string? openListSelector = null,
        string? expandSelector = null,
        bool inlinePage = false,
        CancellationToken ct = default)
    {
        var browser = await GetBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36"
        });

        var page = await context.NewPageAsync();

        LogInformation($"Navigating to listing page {url}");
        await page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = _options.NavigationTimeoutMs
        });
        await DismissCookieBannerAsync(page); // consent is stored on the context → only needed once
        await ExpandAllAsync(page, expandSelector); // open every collapsible section (accordions) if asked

        // ── Inline mode: all departments live on this one page (no per-department subpages) ──
        if (inlinePage)
        {
            var inlineDepts = await ExtractDepartmentsFromInlinePageAsync(page, page.Url, prompt, tips, defaultCountry, ct);
            LogInformation($"Extracted {inlineDepts.Count} department(s) from the page.");
            return RemoveRepeatedContactSets(inlineDepts);
        }

        await OpenListAsync(page, openListSelector); // reveal the list if it's behind a trigger (e.g. a drawer)
        var listingUrl = page.Url;

        // ── Phase 1: pick which clickable elements are the per-department links (tips guide this) ──
        var targets = await DiscoverTargetsAsync(page, prompt, tips, ct);
        LogInformation($"Discovered {targets.Count} department target(s).");
        if (targets.Count == 0)
        {
            LogInformation("No department links found. Refine your tips (which UL/LI/anchor to follow).");
            return new List<DepartmentAnthAuto>();
        }

        // ── Phase 2: open each department (href navigation OR click-driven SPA nav) and extract ──
        var extracted = new List<DepartmentAnthAuto>();
        var total = Math.Min(targets.Count, _options.MaxDepartments);
        var index = 0;
        foreach (var target in targets.Take(_options.MaxDepartments))
        {
            ct.ThrowIfCancellationRequested();
            index++;

            // No try/catch here on purpose: any failure (navigation, Anthropic, parsing) propagates
            // to AnthropicBrowserExtractor.ExtractAsync so it can be surfaced as parseError.
            if (!string.IsNullOrEmpty(target.Href))
            {
                await page.GotoAsync(target.Href!, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = _options.NavigationTimeoutMs });
            }
            else if (!await NavigateByClickAsync(page, target.Id, ct))
            {
                LogInformation($"[{index}/{total}] could not open '{target.Text}' (URL did not change).");
                await ReturnToListingAsync(page, listingUrl);
                continue;
            }

            var dep = await ExtractDepartmentFromPageAsync(page, page.Url, prompt, tips, defaultCountry, ct);
            if (dep != null)
            {
                extracted.Add(dep);
                LogInformation($"[{index}/{total}] {dep.Title} — {dep.AddressLine}, {dep.AddressZip} {dep.AddressCity} | {dep.Telephone} | {dep.Email}");
            }
            else
            {
                LogInformation($"[{index}/{total}] no department data at {page.Url}");
            }

            // Click-driven items navigate within the SPA — return to the listing for the next click.
            if (string.IsNullOrEmpty(target.Href))
                await ReturnToListingAsync(page, listingUrl);
        }

        // ── Phase 3: drop contact sets repeated on >80% of pages (footer / head office) ──
        var cleaned = RemoveRepeatedContactSets(extracted);
        if (cleaned.Count != extracted.Count)
            LogInformation($"Removed {extracted.Count - cleaned.Count} repeated (footer / head-office) record(s).");

        return cleaned;
    }

    // ── Phase 1: choose the department targets ────────────────────────────────

    private async Task<List<LinkCandidate>> DiscoverTargetsAsync(IPage page, string prompt, string? tips, CancellationToken ct)
    {
        var json = await page.EvaluateAsync<string>(DiscoveryJs);
        var candidates = JsonSerializer.Deserialize<List<LinkCandidate>>(json, JsonOpts) ?? new List<LinkCandidate>();
        LogInformation($"Found {candidates.Count} clickable candidate(s) on the listing page.");
        if (candidates.Count == 0) return new List<LinkCandidate>();

        var sb = new StringBuilder();
        foreach (var c in candidates)
            sb.AppendLine($"id={c.Id} | text=\"{c.Text}\" | href={c.Href ?? "(none)"} | aClass=\"{c.Classes}\" | liClass=\"{c.LiClasses}\" | ulClass=\"{c.UlClasses}\"");

        const string system = """
            You identify which page elements link to INDIVIDUAL department / branch / store detail pages.
            You receive a numbered list of clickable elements (anchors and list items) with their visible
            text, href and CSS classes (anchor, parent <li>, parent <ul>).
            Use the user's goal and the human tips to decide which elements are the per-department links.
            Ignore navigation, social, legal, cookie, language and other unrelated links.
            Respond with ONLY a JSON object: {"ids":[<id>, ...]} listing the ids of the department
            detail elements, in page order. No prose, no markdown fences.
            """;

        var user = $"""
            User goal: {prompt}

            Human tips (how to locate the department list — follow these closely; the CSS classes below
            may help you match the right UL / LI / anchor):
            {TipsOrNone(tips)}

            Clickable elements on the page:
            {sb}

            Return a JSON object with an "ids" array holding the ids of the individual department
            detail links only.
            """;

        var reply = await AskClaudeAsync(system, user, ct);
        var ids = ParseIds(reply);
        var byId = candidates.ToDictionary(c => c.Id);

        var targets = new List<LinkCandidate>();
        var seenIds = new HashSet<int>();
        var seenHref = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            if (!seenIds.Add(id) || !byId.TryGetValue(id, out var c)) continue;
            if (!string.IsNullOrEmpty(c.Href) && !seenHref.Add(c.Href!)) continue; // de-dup repeated hrefs
            targets.Add(c);
        }
        return targets;
    }

    /// <summary>
    /// Clicks a tagged, href-less item (React router.push) and waits for the SPA pushState URL to
    /// change. Re-tags the listing DOM first, since returning to the listing re-renders it.
    /// </summary>
    private async Task<bool> NavigateByClickAsync(IPage page, int id, CancellationToken ct)
    {
        await page.EvaluateAsync(DiscoveryJs); // (re)tag the current listing DOM with data-scrape-id
        var before = page.Url;
        await page.Locator($"[data-scrape-id='{id}']").First.ClickAsync(new LocatorClickOptions { Timeout = 8000 });

        // pushState navigation fires no load event — poll the URL instead.
        for (var t = 0; t < 40; t++)
        {
            if (!string.Equals(page.Url, before, StringComparison.OrdinalIgnoreCase)) return true;
            await Task.Delay(200, ct);
        }
        return false;
    }

    /// <summary>Returns to the listing page (history back, falling back to a fresh navigation).</summary>
    private async Task ReturnToListingAsync(IPage page, string listingUrl)
    {
        try
        {
            await page.GoBackAsync(new PageGoBackOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = _options.NavigationTimeoutMs });
            if (string.Equals(page.Url, listingUrl, StringComparison.OrdinalIgnoreCase)) return;
        }
        catch { /* fall through to a fresh navigation */ }

        await page.GotoAsync(listingUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = _options.NavigationTimeoutMs });
    }

    /// <summary>
    /// Extracts a single department from one detail page — handy for testing the extraction rules
    /// against a known URL (e.g. https://www.heidenreich.no/butikker/heidenreich-alta).
    /// </summary>
    public async Task<DepartmentAnthAuto?> ExtractSingleAsync(string url, string prompt, string? tips = null, string defaultCountry = "NO", CancellationToken ct = default)
    {
        var browser = await GetBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36"
        });
        var page = await context.NewPageAsync();

        LogInformation($"Navigating to {url}");
        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = _options.NavigationTimeoutMs });
        await DismissCookieBannerAsync(page);
        return await ExtractDepartmentFromPageAsync(page, url, prompt, tips, defaultCountry, ct);
    }

    /// <summary>
    /// Expands every collapsible section matching <paramref name="selector"/> (e.g. accordion
    /// headers). Clicks the first still-matching element each pass, since expanding one typically
    /// changes/removes it from the match set; bounded to avoid loops. No-op when selector is empty.
    /// </summary>
    private async Task ExpandAllAsync(IPage page, string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector)) return;
        var opened = 0;
        for (var i = 0; i < 40; i++)
        {
            var el = page.Locator(selector).First;
            if (await el.CountAsync() == 0) break;
            try { await el.ClickAsync(new LocatorClickOptions { Timeout = 5000 }); }
            catch { break; } // no longer clickable / none left
            await page.WaitForTimeoutAsync(400);
            opened++;
        }
        LogInformation($"Expanded {opened} collapsible section(s) via '{selector}'.");
    }

    /// <summary>
    /// Clicks <paramref name="selector"/> once to reveal a hidden store list (e.g. a drawer/modal
    /// opener) before discovery. No-op when the selector is null/empty or not present on the page.
    /// </summary>
    private async Task OpenListAsync(IPage page, string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector)) return;
        var trigger = page.Locator(selector).First;
        if (await trigger.CountAsync() == 0)
        {
            LogInformation($"Open-list trigger '{selector}' not found — continuing without it.");
            return;
        }
        await trigger.ClickAsync(new LocatorClickOptions { Timeout = 8000 });
        await page.WaitForTimeoutAsync(1500); // let the revealed list render
        LogInformation($"Opened store list via '{selector}'.");
    }

    /// <summary>Best-effort dismissal of a cookie-consent overlay so it stops intercepting clicks.</summary>
    private async Task DismissCookieBannerAsync(IPage page)
    {
        string[] selectors =
        {
            "#submitAllCategoriesButton",                 // Cookie Information CMP (heidenreich)
            "#coiOverlay button.coi-banner__accept",
            "#onetrust-accept-btn-handler",               // OneTrust CMP
            "button:has-text('Godta alle')",
            "button:has-text('Aksepter alle')",
            "button:has-text('Tillat alle')",
            "button:has-text('Accept all')",
            "[aria-label*='aksepter' i]",
        };

        foreach (var sel in selectors)
        {
            try
            {
                var btn = page.Locator(sel).First;
                if (await btn.CountAsync() > 0 && await btn.IsVisibleAsync())
                {
                    await btn.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
                    await page.WaitForTimeoutAsync(400);
                    LogInformation($"Dismissed cookie banner via '{sel}'.");
                    return;
                }
            }
            catch { /* try the next selector */ }
        }
    }

    // ── Phase 2: per-page extraction ──────────────────────────────────────────

    private async Task<DepartmentAnthAuto?> ExtractDepartmentFromPageAsync(IPage page, string url, string prompt, string? tips, string defaultCountry, CancellationToken ct)
    {
        var payload = await page.EvaluateAsync<string>(PagePayloadJs);

        const string system = """
            You extract ONE department / branch / store contact record from a single detail page.

            Rules (priority order):
            - Title: prefer the main <h1>/<h2> heading (or an element with a title/heading class). It is
              the branch / company name.
            - Take the contact details shown at the TOP of the page — the first / primary address, phone
              and email. The tels and mails lists are given in top-to-bottom DOM order.
            - If several phones exist, take the first / primary one (the branch's main number).
            - IGNORE lists of individual people / staff ("Kontaktpersoner", contact persons, entries that
              are personal names with job titles). They are NOT the department contact.
            - IGNORE footer / head-office details ("hovedkontor", main office) that repeat on every page.
            - Split the address into street line, 4-digit postal code and city when possible.
            - fullAddress is the full address as one string in the form "AddressLine, AddressZip AddressCity".

            Respond with ONLY this JSON (no prose, no fences):
            {"found":true,"title":"","fullAddress":"","addressLine":"","addressZip":"","addressCity":"","telephone":"","email":""}
            If the page has no department contact info, respond {"found":false}.
            """;

        var user = $"""
            User goal: {prompt}

            Human tips:
            {TipsOrNone(tips)}

            Page URL: {url}
            Page content (JSON — title, headings, tels and mails in DOM order, and visible text):
            {payload}
            """;

        var reply = await AskClaudeAsync(system, user, ct);
        var dep = ParseDepartment(reply);
        if (dep == null) return null;

        dep.Url = url;
        dep.Telephone = FormatPhoneE164(dep.Telephone, defaultCountry);
        if (string.IsNullOrWhiteSpace(dep.FullAddress))
            dep.FullAddress = BuildFullAddress(dep);
        return dep;
    }

    // ── Inline extraction: MANY departments from ONE page (no per-department subpages) ──

    private async Task<List<DepartmentAnthAuto>> ExtractDepartmentsFromInlinePageAsync(IPage page, string url, string prompt, string? tips, string defaultCountry, CancellationToken ct)
    {
        var payload = await page.EvaluateAsync<string>(InlinePagePayloadJs);

        const string system = """
            You extract ALL department / office contact records from a SINGLE page that lists many
            offices inline (there are no per-office subpages). Return EVERY physical office as its own record.

            Rules:
            - Title: the office / location name (usually a place or city name).
            - Split each office's address into street line, 4-digit postal code and city.
            - telephone / email: use the office's own if it shows one; otherwise use the contact person
              given for that office's region/section (e.g. "Kontaktperson Region ..."). Prefer a real
              value over blank. Phones may be plain text like "+47 45 44 29 43".
            - fullAddress = "AddressLine, AddressZip AddressCity".
            - Ignore the top index/navigation list and any site header/footer that are not offices.
            - Do NOT invent data — leave a field "" if it is truly absent.

            Respond with ONLY this JSON (no prose, no fences):
            {"departments":[{"title":"","fullAddress":"","addressLine":"","addressZip":"","addressCity":"","telephone":"","email":""}]}
            If the page has no offices, respond {"departments":[]}.
            """;

        var user = $"""
            User goal: {prompt}

            Human tips:
            {TipsOrNone(tips)}

            Page URL: {url}
            Page content (JSON — visible text of the whole page plus any mailto/tel links):
            {payload}
            """;

        // Allow a larger response — a page can hold many offices.
        var reply = await AskClaudeAsync(system, user, ct, maxTokensOverride: Math.Max(_options.MaxTokens, 4096));
        var deps = ParseDepartments(reply);

        foreach (var dep in deps)
        {
            dep.Url = url;
            dep.Telephone = FormatPhoneE164(dep.Telephone, defaultCountry);
            if (string.IsNullOrWhiteSpace(dep.FullAddress))
                dep.FullAddress = BuildFullAddress(dep);
        }
        return deps;
    }

    // No try/catch: a malformed model response throws (JsonException) and surfaces as parseError.
    // Accepts either a bare array or {"departments":[...]}. String values are HTML-decoded.
    private static List<DepartmentAnthAuto> ParseDepartments(string reply)
    {
        var list = new List<DepartmentAnthAuto>();
        using var doc = JsonDocument.Parse(StripFences(reply));
        var root = doc.RootElement;

        JsonElement arr;
        if (root.ValueKind == JsonValueKind.Array) arr = root;
        else if (!root.TryGetProperty("departments", out arr) || arr.ValueKind != JsonValueKind.Array) return list;

        foreach (var e in arr.EnumerateArray())
        {
            string Get(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                ? WebUtility.HtmlDecode(v.GetString()!).Trim()
                : string.Empty;

            var title = Get("title");
            var line = Get("addressLine");
            if (title.Length == 0 && line.Length == 0) continue; // skip empty rows

            list.Add(new DepartmentAnthAuto
            {
                Title = title,
                FullAddress = Get("fullAddress"),
                AddressLine = line,
                AddressZip = Get("addressZip"),
                AddressCity = Get("addressCity"),
                Telephone = Get("telephone"),
                Email = Get("email"),
            });
        }
        return list;
    }

    /// <summary>Formats a phone to E.164 (e.g. +4755538600); returns the trimmed original if unparseable.</summary>
    private static string FormatPhoneE164(string? raw, string defaultCountry)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        try
        {
            var util = PhoneNumberUtil.GetInstance();
            var number = util.Parse(raw, defaultCountry);
            if (util.IsValidNumber(number))
                return util.Format(number, PhoneNumberFormat.E164);
        }
        catch (NumberParseException) { /* fall through */ }
        return raw.Trim();
    }

    private static string BuildFullAddress(DepartmentAnthAuto d)
    {
        var line = d.AddressLine.Trim();
        var zipCity = string.Join(" ", new[] { d.AddressZip, d.AddressCity }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
        return string.Join(", ", new[] { line, zipCity }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    // ── Phase 3: repetition filter ────────────────────────────────────────────

    /// <summary>
    /// Removes contact sets that appear on more than 80% of pages — these are almost always the
    /// footer / head-office details repeated site-wide rather than a real department.
    /// </summary>
    private List<DepartmentAnthAuto> RemoveRepeatedContactSets(List<DepartmentAnthAuto> deps)
    {
        if (deps.Count < 5) return deps; // too few pages for the 80% rule to be meaningful

        var counts = deps.GroupBy(Signature).ToDictionary(g => g.Key, g => g.Count());
        var threshold = deps.Count * 0.8;
        return deps.Where(d => counts[Signature(d)] <= threshold).ToList();

        // URL is intentionally excluded — it is unique per page and would defeat the repetition check.
        static string Signature(DepartmentAnthAuto d) => string.Join("|",
            new[] { d.Title, d.AddressLine, d.AddressZip, d.AddressCity, d.Telephone, d.Email }
                .Select(s => (s ?? string.Empty).Trim().ToLowerInvariant()));
    }

    // ── Claude helpers ────────────────────────────────────────────────────────

    private async Task<string> AskClaudeAsync(string system, string user, CancellationToken ct, int? maxTokensOverride = null)
    {
        var callNumber = Interlocked.Increment(ref _callCount);

#pragma warning disable CS0618 // Temperature is deprecated for models after Opus 4.6, but is an intentional config option (claude-sonnet-4-6 accepts 0).
        var parms = new MessageCreateParams
        {
            Model = _options.Model,
            MaxTokens = maxTokensOverride ?? _options.MaxTokens,
            Temperature = _options.Temperature,
            System = system,
            Messages = new List<MessageParam> { new() { Role = Role.User, Content = user } }
        };
#pragma warning restore CS0618

        var reply = _options.Stream
            ? await StreamTextAsync(parms, ct)
            : await CreateTextAsync(parms, ct);

        AppendCallLog(callNumber, system, user, reply);
        return reply;
    }

    private async Task<string> CreateTextAsync(MessageCreateParams parms, CancellationToken ct)
    {
        var response = await _claude.Messages.Create(parms, cancellationToken: ct);
        foreach (var block in response.Content)
            if (block.TryPickText(out var tb)) return tb.Text;
        return string.Empty;
    }

    private async Task<string> StreamTextAsync(MessageCreateParams parms, CancellationToken ct)
    {
        var sb = new StringBuilder();
        await foreach (var evt in _claude.Messages.CreateStreaming(parms, ct))
            if (evt.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var td))
                sb.Append(td.Text);
        return sb.ToString();
    }

    // ── Claude call logging ───────────────────────────────────────────────────

    private int _callCount;
    private readonly object _logLock = new();

    /// <summary>Appends one Claude call (number, prompts, response) to the configured log file.</summary>
    private void AppendCallLog(int callNumber, string system, string user, string response)
    {
        if (!_options.AnthropicAutomationCallLog || string.IsNullOrWhiteSpace(_options.AnthropicAutomationCallLogPath)) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine(new string('=', 90));
            sb.AppendLine($"AI CALL #{callNumber}  |  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  |  model={_options.Model}  maxTokens={_options.MaxTokens}  stream={_options.Stream}");
            sb.AppendLine(new string('-', 90));
            sb.AppendLine("SYSTEM PROMPT:");
            sb.AppendLine(system);
            sb.AppendLine();
            sb.AppendLine("USER PROMPT:");
            sb.AppendLine(user);
            sb.AppendLine();
            sb.AppendLine("RESPONSE:");
            sb.AppendLine(response);
            sb.AppendLine();

            lock (_logLock)
                File.AppendAllText(_options.AnthropicAutomationCallLogPath!, sb.ToString());
        }
        catch (Exception ex)
        {
            LogInformation($"call-log write failed: {ex.Message}");
        }
    }

    // No try/catch: a malformed model response throws (JsonException) and surfaces as parseError.
    private static List<int> ParseIds(string reply)
    {
        var ids = new List<int>();
        using var doc = JsonDocument.Parse(StripFences(reply));
        if (doc.RootElement.TryGetProperty("ids", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
                if (e.TryGetInt32(out var n)) ids.Add(n);
        return ids;
    }

    // No try/catch: a malformed model response throws (JsonException) and surfaces as parseError.
    // String values are HTML-decoded so Norwegian characters (æ/ø/å) are correct in the result list.
    private static DepartmentAnthAuto? ParseDepartment(string reply)
    {
        using var doc = JsonDocument.Parse(StripFences(reply));
        var r = doc.RootElement;
        if (r.TryGetProperty("found", out var f) && f.ValueKind == JsonValueKind.False) return null;

        string Get(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            ? WebUtility.HtmlDecode(v.GetString()!).Trim()
            : string.Empty;

        var title = Get("title");
        var line = Get("addressLine");
        if (title.Length == 0 && line.Length == 0) return null; // nothing useful

        return new DepartmentAnthAuto
        {
            Title = title,
            FullAddress = Get("fullAddress"),
            AddressLine = line,
            AddressZip = Get("addressZip"),
            AddressCity = Get("addressCity"),
            Telephone = Get("telephone"),
            Email = Get("email"),
        };
    }

    private static string StripFences(string s)
    {
        var clean = s.Trim();
        if (clean.StartsWith("```"))
        {
            var nl = clean.IndexOf('\n');
            if (nl >= 0) clean = clean[(nl + 1)..];
            if (clean.EndsWith("```")) clean = clean[..^3];
        }
        return clean.Trim();
    }

    private static string TipsOrNone(string? tips) => string.IsNullOrWhiteSpace(tips) ? "(none provided)" : tips!.Trim();

    private void LogInformation(string s)
    {
        if (_managerOptions.Value.VerboseChainProcess)
            _logger.LogInformation(s);
    }

    // ── Browser-side JS ───────────────────────────────────────────────────────

    /// <summary>
    /// Tags every clickable candidate with a stable data-scrape-id and returns a JSON array of
    /// {id, href, text, classes, liClasses, ulClasses}. Anchors first (href present), then clickable
    /// &lt;li&gt; elements that have no anchor descendant (JS navigation; href null).
    /// </summary>
    private const string DiscoveryJs = """
        () => {
          const out = [];
          let id = 0;
          const trunc = (s, n) => (s == null ? '' : s.toString()).trim().slice(0, n);
          document.querySelectorAll('a[href]').forEach(a => {
            const href = a.href || '';
            if (!href || href.startsWith('javascript:') || href.startsWith('mailto:') || href.startsWith('tel:') || href.startsWith('#')) return;
            a.setAttribute('data-scrape-id', String(id));
            const li = a.closest('li');
            const ul = a.closest('ul');
            out.push({ id, href, text: trunc(a.innerText, 120), classes: trunc(a.className, 300), liClasses: trunc(li && li.className, 300), ulClasses: trunc(ul && ul.className, 300) });
            id++;
          });
          document.querySelectorAll('li').forEach(li => {
            if (li.querySelector('a[href]')) return;
            li.setAttribute('data-scrape-id', String(id));
            const ul = li.closest('ul');
            out.push({ id, href: null, text: trunc(li.innerText, 120), classes: trunc(li.className, 300), liClasses: trunc(li.className, 300), ulClasses: trunc(ul && ul.className, 300) });
            id++;
          });
          return JSON.stringify(out.slice(0, 800));
        }
        """;

    /// <summary>Returns a compact JSON payload of a department detail page for extraction.</summary>
    private const string PagePayloadJs = """
        () => {
          const trunc = (s, n) => (s == null ? '' : s.toString()).trim().slice(0, n);
          const headings = Array.from(document.querySelectorAll('h1,h2,h3,h4'))
            .map(h => h.tagName + ': ' + trunc(h.innerText, 200))
            .filter(t => t.length > 4);
          const tels = [...new Set(Array.from(document.querySelectorAll('a[href^="tel:"]'))
            .map(a => (a.getAttribute('href') || '').replace('tel:', '').trim()).filter(Boolean))];
          const mails = [...new Set(Array.from(document.querySelectorAll('a[href^="mailto:"]'))
            .map(a => (a.getAttribute('href') || '').replace('mailto:', '').split('?')[0].trim()).filter(Boolean))];
          let text = (document.body.innerText || '').replace(/[ \t]+\n/g, '\n').replace(/\n{2,}/g, '\n').trim();
          // Keep the head AND the tail: some sites (e.g. classicnorway hotels) put the contact block in the footer.
          if (text.length > 6000) text = text.slice(0, 3000) + '\n…\n' + text.slice(-3000);
          return JSON.stringify({ title: document.title, headings, tels, mails, text });
        }
        """;

    /// <summary>Payload for inline mode: the whole page's visible text plus any mailto/tel links.</summary>
    private const string InlinePagePayloadJs = """
        () => {
          const mails = [...new Set(Array.from(document.querySelectorAll('a[href^="mailto:"]'))
            .map(a => (a.getAttribute('href') || '').replace('mailto:', '').split('?')[0].trim()).filter(Boolean))];
          const tels = [...new Set(Array.from(document.querySelectorAll('a[href^="tel:"]'))
            .map(a => (a.getAttribute('href') || '').replace('tel:', '').trim()).filter(Boolean))];
          let text = (document.body.innerText || '').replace(/[ \t]+\n/g, '\n').replace(/\n{2,}/g, '\n').trim();
          if (text.length > 14000) text = text.slice(0, 14000);
          return JSON.stringify({ mails, tels, text });
        }
        """;

    // ── Browser lifecycle ─────────────────────────────────────────────────────

    private async Task<IBrowser> GetBrowserAsync()
    {
        if (_browser != null) return _browser;
        _playwright = await Playwright.CreateAsync();

        var launchOptions = new BrowserTypeLaunchOptions { Headless = _options.Headless };

        // Empty path → use Playwright's default (bundled) browser; otherwise launch the given executable
        // (e.g. a system Chromium on Linux: /usr/bin/ungoogled-chromium).
        if (!string.IsNullOrWhiteSpace(_options.PlaywrightBrowserExecutablePath))
        {
            launchOptions.ExecutablePath = _options.PlaywrightBrowserExecutablePath;
            LogInformation($"Using custom browser executable: {_options.PlaywrightBrowserExecutablePath}");
        }

        _browser = await _playwright.Chromium.LaunchAsync(launchOptions);
        return _browser;
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser != null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }

    // ── Discovery payload model ───────────────────────────────────────────────

    private sealed class LinkCandidate
    {
        public int Id { get; set; }
        public string? Href { get; set; }
        public string Text { get; set; } = string.Empty;
        public string Classes { get; set; } = string.Empty;
        public string LiClasses { get; set; } = string.Empty;
        public string UlClasses { get; set; } = string.Empty;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
}
