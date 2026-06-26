# AgentScraper

Web scraper for store/department finder pages: **Playwright** (renders JS/SPA) + **Claude API**
(picks the right links, extracts the contact fields) → structured `Department` records.

## How it works

A tips-driven, three-phase pipeline (no fragile accessibility-snapshot loop — real DOM data stays
attached to each element so the click path is never lost):

```
1. Discovery   Navigate to the listing page → enumerate every clickable element
               (anchors + clickable <li>) WITH its real href, text and CSS classes →
               Claude picks the per-department links, guided by the human `tips`.

2. Extraction  Open each department (href navigation, or click the element when it is a
               JS-only <li> and capture the SPA URL) → collect a focused payload
               (headings, tel:/mailto: links in DOM order, visible text) → Claude
               extracts ONE record: title from the heading, the top/primary address +
               phone + email, ignoring staff ("Kontaktpersoner") lists and the footer.

3. Filtering   Drop any contact set that repeats on >80% of pages (site-wide footer /
               head-office details that leaked through).
```

The human **`tips`** describe where the department list lives and which element to follow
(e.g. which `<ul>` / `<li>` / anchor, by class or text). This is what lets the agent find the
correct click path on sites where the elements carry no `href` and navigate via JavaScript.

## Setup

### 1. Install dependencies
```bash
dotnet restore
```

### 2. Install Playwright browsers (one-time)
```bash
dotnet build
pwsh bin/Debug/net9.0/playwright.ps1 install chromium
# or on Linux/Mac:
# bin/Debug/net9.0/playwright.sh install chromium
```

### 3. Set API key
```bash
# Windows
set ANTHROPIC_API_KEY=sk-ant-...

# Linux/Mac
export ANTHROPIC_API_KEY=sk-ant-...
```

### 4. Run
```bash
dotnet run
```

Output: console + `departments.json`

## Usage for a different site

```csharp
var departments = await scraper.ExtractAsync(
    url:    "https://some-other-site.no/stores",
    prompt: "Find every store and open its detail page. " +
            "Extract title, addressLine, addressCity, addressZip, telephone, email.",
    tips:   "The store list is a <ul> with class '...'. Each store is an <li> with class '...'. " +
            "Follow every such <li> to open its detail page and read the contact data there."
);
```

`tips` is optional but is the main knob for a new site: point the agent at the right list/element.
To validate the extraction rules against a single known detail page:

```csharp
var one = await scraper.ExtractSingleAsync(
    url:    "https://some-other-site.no/stores/oslo",
    prompt: "Extract this branch's title, addressLine, addressCity, addressZip, telephone, email.");
```

## Options

```csharp
var options = new AgentScraperOptions
{
    Headless       = false, // set true to run without a visible browser
    Verbose        = true,  // print agent decisions to console
    MaxDepartments = 300,   // safety cap on how many detail pages to visit
};
```

## NuGet packages

| Package | Purpose |
|---|---|
| `Anthropic` v12+ | Official Anthropic C# SDK |
| `Microsoft.Playwright` | Browser automation |
