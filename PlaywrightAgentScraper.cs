using System.Text.Json;
using AgentScraper.Models;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Playwright;

namespace AgentScraper;

/// <summary>
/// Agent loop:
///   1. Render page with Playwright (handles JS/SPA)
///   2. Snapshot accessibility tree → send to Claude with prompt
///   3. Claude returns next action: click selector | extract data | done
///   4. Execute action in Playwright → repeat from step 2
/// </summary>
public sealed class PlaywrightAgentScraper : IAgentScraper, IAsyncDisposable
{
    private readonly AnthropicClient _claude;
    private readonly AgentScraperOptions _options;

    // Playwright lifetime — created lazily, shared across calls
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public PlaywrightAgentScraper(AnthropicClient claude, AgentScraperOptions? options = null)
    {
        _claude = claude;
        _options = options ?? new AgentScraperOptions();
    }

    public async Task<List<Department>> ExtractAsync(
        string url,
        string prompt,
        CancellationToken ct = default)
    {
        var browser = await GetBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36"
        });

        var page = await context.NewPageAsync();

        if (_options.Verbose)
            Console.WriteLine($"[Agent] Navigating to {url}");

        await page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = _options.NavigationTimeoutMs
        });

        // Conversation history sent to Claude each iteration
        var messages = new List<MessageParam>();
        var allDepartments = new List<Department>();
        var clickedSelectors = new HashSet<string>(); // avoid re-clicking

        for (int iteration = 0; iteration < _options.MaxIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();

            var snapshot = await GetAccessibilitySnapshotAsync(page);

            if (_options.Verbose)
                Console.WriteLine($"[Agent] Iteration {iteration + 1}, snapshot length: {snapshot.Length} chars");

            var userMessage = BuildUserMessage(snapshot, prompt, iteration);
            messages.Add(new MessageParam { Role = Role.User, Content = userMessage });

            var response = await _claude.Messages.Create(new MessageCreateParams
            {
                Model = "claude-sonnet-4-6",
                MaxTokens = 2048,
                System = SystemPrompt,
                Messages = messages
            }, cancellationToken: ct);

            var assistantText = "";
            foreach (var block in response.Content)
                if (block.TryPickText(out var tb)) { assistantText = tb.Text; break; }

            // Add assistant reply to conversation history
            messages.Add(new MessageParam { Role = Role.Assistant, Content = assistantText });

            if (_options.Verbose)
                Console.WriteLine($"[Agent] Claude response:\n{assistantText}\n");

            var action = ParseAction(assistantText);

            if (action == null)
            {
                Console.WriteLine("[Agent] Could not parse action — stopping.");
                break;
            }

            if (action.Data != null)
                allDepartments.AddRange(action.Data);

            switch (action.Type)
            {
                case AgentActionType.Done:
                    if (_options.Verbose)
                        Console.WriteLine($"[Agent] Done. Reason: {action.Reason}");
                    return allDepartments;

                case AgentActionType.Extract:
                    // Claude extracted what it could from current view — continue to let it decide next step
                    if (_options.Verbose)
                        Console.WriteLine($"[Agent] Extracted {action.Data?.Count ?? 0} departments so far.");
                    break;

                case AgentActionType.Click:
                    if (action.Selector == null) break;
                    if (clickedSelectors.Contains(action.Selector))
                    {
                        if (_options.Verbose)
                            Console.WriteLine($"[Agent] Already clicked '{action.Selector}', skipping.");
                        break;
                    }
                    await ClickElementAsync(page, action.Selector);
                    clickedSelectors.Add(action.Selector);
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                    break;

                case AgentActionType.Scroll:
                    await page.EvaluateAsync("window.scrollBy(0, 600)");
                    await Task.Delay(500, ct);
                    break;
            }
        }

        Console.WriteLine("[Agent] Max iterations reached.");
        return allDepartments;
    }

    // ── Accessibility snapshot ────────────────────────────────────────────────

    private static async Task<string> GetAccessibilitySnapshotAsync(IPage page)
    {
        // AriaSnapshot returns a YAML-like text representation of the page — cheap for LLM input
        return await page.Locator("body").AriaSnapshotAsync() ?? "<empty snapshot>";
    }

    // ── Prompt building ───────────────────────────────────────────────────────

    private static string BuildUserMessage(string snapshot, string userPrompt, int iteration)
    {
        var prefix = iteration == 0
            ? $"User goal: {userPrompt}\n\nThis is the first page view."
            : "The page has updated after your last action. Continue working toward the goal.";

        return $"""
            {prefix}

            Current page accessibility tree:
            ```
            {snapshot}
            ```

            Respond with a JSON action object as specified in your instructions.
            """;
    }

    private const string SystemPrompt = """
        You are a browser automation agent. Your job is to extract department data from web pages.

        Each department has: title, addressLine, addressCity, addressZip, telephone, email.

        On each turn you receive the current page's accessibility tree and must respond with EXACTLY one JSON action:

        To click an element (use text label or partial label as selector):
        {"action":"click","selector":"Button or link text here","reason":"why you click this"}

        To record extracted departments (when you can see department data in the current view):
        {"action":"extract","reason":"found N departments","data":[{"title":"...","addressLine":"...","addressCity":"...","addressZip":"...","telephone":"...","email":"..."}]}

        To scroll down if content may be hidden below:
        {"action":"scroll","reason":"more content may be below"}

        When all departments have been found and extracted:
        {"action":"done","reason":"all departments extracted","data":[...complete final list...]}

        Rules:
        - Always respond with valid JSON only. No prose, no markdown fences.
        - For "done" and "extract", always include the full data array found SO FAR.
        - If a page has region/county/municipality filters, click each one to reveal departments.
        - Avoid clicking the same element twice.
        - If the page shows a list with no further filters needed, go straight to extract/done.
        - Missing fields should be empty string "".
        """;

    // ── Action parsing ────────────────────────────────────────────────────────

    private static AgentAction? ParseAction(string json)
    {
        try
        {
            // Strip markdown fences if Claude adds them despite instructions
            var clean = json.Trim();
            if (clean.StartsWith("```")) clean = clean.Split('\n', 2)[1];
            if (clean.EndsWith("```")) clean = clean[..^3];

            using var doc = JsonDocument.Parse(clean.Trim());
            var root = doc.RootElement;

            var actionType = root.GetProperty("action").GetString() switch
            {
                "click"   => AgentActionType.Click,
                "extract" => AgentActionType.Extract,
                "done"    => AgentActionType.Done,
                "scroll"  => AgentActionType.Scroll,
                _         => throw new InvalidOperationException("Unknown action")
            };

            var selector = root.TryGetProperty("selector", out var s) ? s.GetString() : null;
            var reason   = root.TryGetProperty("reason",   out var r) ? r.GetString() : null;

            List<Department>? data = null;
            if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
            {
                data = dataEl.Deserialize<List<Department>>(new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }

            return new AgentAction(actionType, selector, reason, data);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Agent] Failed to parse action JSON: {ex.Message}\nRaw: {json}");
            return null;
        }
    }

    // ── Click helper ──────────────────────────────────────────────────────────

    private async Task ClickElementAsync(IPage page, string selector)
    {
        try
        {
            // Try as CSS selector first, then fall back to visible text search
            var element = page.Locator(selector).First;
            var count = await element.CountAsync();

            if (count == 0)
                element = page.GetByText(selector, new PageGetByTextOptions { Exact = false }).First;

            if (_options.Verbose)
                Console.WriteLine($"[Agent] Clicking: {selector}");

            await element.ClickAsync(new LocatorClickOptions { Timeout = 5000 });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Agent] Click failed for '{selector}': {ex.Message}");
        }
    }

    // ── Browser lifecycle ─────────────────────────────────────────────────────

    private async Task<IBrowser> GetBrowserAsync()
    {
        if (_browser != null) return _browser;
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = _options.Headless
        });
        return _browser;
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser != null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }

}
