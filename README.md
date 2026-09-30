# AgentScraper (Advista.ScraperA)

Scrapes **store / department / office finder pages** on Norwegian sites and returns structured
contact records. **Playwright** renders the JS/SPA page; the **Claude API** picks the right links
and extracts the fields. Human **`tips`** per site tell Claude where the departments live.

Namespace: `Advista.Tasks._180no.CrawlerLocators.Components.Anthropic.Services`.
Distinct from ScraperB (the RSC-parsing tool in `D:\aReposB\CLAUDE.md`).

## Entry point

```csharp
IAnthropicBrowserExtractor extractor = new AnthropicBrowserExtractor(
    Options.Create(anthropicBrowserOptions),   // IOptions<AnthropicBrowserOptions>
    Options.Create(managerOptions),            // IOptions<ManagerOptions>
    logger);                                   // ILogger<AnthropicBrowserExtractor>

var (departments, parseError) = await extractor.ExtractAsync(
    url:              "https://www.xl-bygg.no/butikker",
    tips:             "…where the department links are…",
    defaultCountry:   "NO",         // phone region for E.164
    openListSelector: null,         // click-once to reveal a hidden list (drawer/modal)
    expandSelector:   null,         // click every match to expand accordions
    inlinePage:       false);       // true = all departments are on this one page
```

- **`departments`** — `List<DepartmentAnthAuto>` (empty, never null).
- **`parseError`** — empty on success; otherwise a displayable message. Errors are **not** thrown:
  the inner pipeline deliberately avoids try/catch so failures (nav timeout, Anthropic error,
  bad JSON) bubble to the single try/catch in `AnthropicBrowserExtractor.ExtractAsync`.

The extraction **prompt** is a private const inside `AnthropicBrowserExtractor` (callers don't pass it).

## `DepartmentAnthAuto`

`Title`, `FullAddress` (`"line, zip city"`), `AddressLine`, `AddressZip`, `AddressCity`,
`Telephone` (**E.164**, via libphonenumber, `defaultCountry` when no `+` prefix), `Email`,
`Url` (the page the record came from). String fields are HTML-decoded (correct `æ/ø/å`).

## How it works — two modes

**Navigate mode** (default) — departments have their own detail pages:

```
1. Load listing → dismiss cookie banner → (expandSelector) → (openListSelector)
2. Discovery   Tag every clickable element (anchors + clickable <li>) with data-scrape-id,
               capture href/text/classes → Claude picks the department targets (guided by tips).
3. Extraction  For each target: navigate (href) OR click a href-less JS <li> and capture the
               pushState URL → read a focused payload (headings, tel:/mailto:, text) →
               Claude extracts ONE record (top contact; ignores staff lists & footer).
               Text over 6000 chars is sent as first 3000 + last 3000, so a footer contact
               block survives. The "ignore footer" rule can be overridden per site via tips.
4. Filter      Drop any contact set repeating on >80% of pages (footer / head-office leak).
```

**Inline mode** (`inlinePage: true`) — all departments live on one page (no subpages):

```
1. Load page → dismiss cookies → expand accordions (expandSelector)
2. Read the whole page's text + mailto/tel links → Claude returns a LIST of departments
   ({"departments":[…]}) in one call → same E.164 / FullAddress / filter post-processing.
```

## The per-site knobs

`tips` describe the department elements to Claude (it sees each candidate's **href, visible text,
CSS classes** — so an **href pattern** + class tokens are the most useful hints; ancestor `<div>`
classes are NOT exposed, so don't rely on them). The other knobs handle page mechanics:

| Knob | When to use |
|------|-------------|
| `tips` | Always — which elements are department links / how to read offices |
| `openListSelector` | Store list hidden behind a trigger, revealed by **one** click (drawer/modal) |
| `expandSelector` | Collapsible sections (**accordions**) — every match is clicked to expand |
| `inlinePage` | Departments are inline on one page (no per-department subpages) |

## Site playbook (verified working)

| Site | Pattern | Knobs |
|------|---------|-------|
| **heidenreich.no/butikker** | href-less JS `<li>` → click + capture pushState URL | tips only |
| **xl-bygg.no/butikker** | real `<a href="/butikker/xl-bygg-…">` anchors | tips only |
| **biltema.no** | store list hidden in a drawer | `openListSelector="#react__storeselector"` |
| **bufarkompetanse.no/kontakt** | 27 offices inline, in region accordions; regional offices inherit the "Kontaktperson Region …" phone/email | `expandSelector="[data-framer-name$='closed']"`, `inlinePage=true` |
| **classicnorway.no/hoteller** | list in a "Velg hotell" dropdown; `<a class="post-name">` links go to **each hotel's own domain**; contact block is in that site's **footer** (tips say so). 34/35 hotels — Torreby Slott (SE) has no contact info on its page; a few footers carry only zip+city | `openListSelector=".term-post-list-trigger"` |
| ~~carlings.com~~ | **WAF-blocked** (403 on JS bundles) — real Chrome + stealth + reload all failed. Parked; needs a residential proxy. |

`Program.cs` keeps all sites as commented blocks; uncomment one to make it active.

## Adding a new site — workflow

1. **Inspect the listing page** (browser devtools / JS): find the department anchors and their
   href pattern + classes (`document.querySelectorAll('a.some-class')` count should equal the
   number of departments). Check whether the list is hidden (→ `openListSelector`), in accordions
   (→ `expandSelector`), or inline with no subpages (→ `inlinePage`). Hidden anchors are still
   discovered — the discovery JS reads the whole DOM, not only visible elements.
2. **Inspect 3–5 detail pages**: where is the contact block (top? footer?), how long is the page
   text, are there `tel:`/`mailto:` links? Note sites that redirect or have no contact info.
3. **Write tips**: href pattern + class tokens for the links; what to exclude (non-department
   entries in the list); where the contact block sits and what is NOT an address/phone.
4. Add the block to `Program.cs` (comment out the previous active site), run, then evaluate.
5. Add a row to the playbook above.

Tip patterns that proved useful:
- Links to **external domains** (classicnorway) are fine — say so in tips so Claude doesn't
  filter them out as "not the main site".
- **Footer contact**: when each department is its own site, tell Claude the footer block IS the
  department contact (otherwise the default rule ignores footers).
- **Distance lines** ("Togstasjon 200 m", "Lufthavn 62 km") get mistaken for addresses — tell
  Claude they are not addresses and to leave `addressLine` empty.
- **Inherited contacts**: when an office lacks its own phone/email, name the fallback
  (bufarkompetanse: the "Kontaktperson Region …").

## Evaluating a run

- When started from **Visual Studio**, the working directory is `bin\Debug\net9.0\` — that's where
  `departments.json`, `log_anthropicautomation.txt` and the `appsettings.json` lookup live (not the
  project root). `dotnet run` from the project folder uses the project root.
- `log_anthropicautomation.txt` (enabled by `AnthropicAutomationCallLog`) holds every Claude call:
  system + user prompt (incl. the page payload JSON) + response. It is **appended** across runs.
  - Call #1 of a navigate run is discovery → its response `{"ids":[…]}` shows which links were
    picked; count vs. the expected number of department links.
  - Missing record → find the call whose `Page URL:` matches; `{"found":false}` means no contact on
    that page.
  - Missing/odd field → read that call's payload `text` (tail = footer) to see whether the data was
    on the page (site gap) or was misread (fix with tips).
- Expected count = discovered links − tips-excluded entries − `found:false` pages − repeated-contact
  filter (>80%, only when ≥5 records).

## Configuration

Bound from the `AnthropicBrowserOptions` and `ManagerOptions` appsettings sections. Loaded layered
so secrets stay out of git:

```
appsettings.development.json   committed template (placeholder ApiKey)
appsettings.{ENVIRONMENT}.json  e.g. appsettings.Production.Linux.json (set DOTNET_ENVIRONMENT)
appsettings.json               real key — GITIGNORED — wins on overlapping keys
```

`AnthropicBrowserOptions`: `ApiKey`, `Model`, `MaxTokens`, `MaxRetries`, `Temperature`, `Stream`,
`HttpTimeoutMinutes`, `Headless`, `NavigationTimeoutMs`, `MaxDepartments`,
`AnthropicAutomationCallLog` (+`…Path`), `AutoInstallBrowsers`,
`PlaywrightBrowserExecutablePath` (empty = bundled browser; e.g. `/usr/bin/ungoogled-chromium` on
Linux) and `PuppeteerBrowserExecutablePath` (carried for a sibling tool; unused here).

`ManagerOptions`: `VerboseChainProcess` — gates the Serilog progress logging.

## Running

```bash
dotnet run                       # scrapes the active site in Program.cs → departments.json
dotnet run -- --install-browsers # download Chromium (add --with-deps + sudo on a bare server)
```

## Deploying to Linux

A Windows build only bundles the Windows Playwright driver → "Driver not found: …/linux-x64/node".
Publish for linux-x64 (`scripts/publish-linux.ps1` or `dotnet publish -c Release -r linux-x64`),
then on the box run `scripts/setup-linux.sh` (chmod the driver + install the browser) and set
`Headless: true`. Full recipe in **`DEPLOY-linux.md`**.

## NuGet packages

| Package | Purpose |
|---|---|
| `Anthropic` | Anthropic C# SDK |
| `Microsoft.Playwright` | Browser automation |
| `libphonenumber-csharp` | Phone → E.164 |
| `Serilog` + `Serilog.Extensions.Logging` + `Serilog.Sinks.Console` | Logging |
| `Microsoft.Extensions.Configuration.*`, `.Logging`, `.Options` | Config binding, DI-style options/logger |
