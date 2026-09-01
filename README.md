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
| ~~carlings.com~~ | **WAF-blocked** (403 on JS bundles) — real Chrome + stealth + reload all failed. Parked; needs a residential proxy. |

`Program.cs` keeps all sites as commented blocks; uncomment one to make it active.

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
