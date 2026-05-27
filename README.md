# AgentScraper

Agentic web scraper: **Playwright** (renders JS/SPA) + **Claude API** (understands page, decides what to click) → structured `Department` records.

## How it works

```
Navigate to URL (Playwright, NetworkIdle)
    ↓
Accessibility tree snapshot (text-based DOM — cheap for LLM)
    ↓
Claude decides: click X | extract data | scroll | done
    ↓
Playwright executes action
    ↓
Repeat until Claude says "done" or MaxIterations reached
```

No hardcoded selectors. The prompt drives everything — works across different sites.

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
    url: "https://some-other-site.no/branches",
    prompt: "Find all branch offices. Click any region filters to reveal all locations. " +
            "Extract title, addressLine, addressCity, addressZip, telephone, email."
);
```

Only the `url` and `prompt` change. No selector tuning needed.

## Options

```csharp
var options = new AgentScraperOptions
{
    Headless      = false, // set false to watch the browser work
    Verbose       = true,  // print agent decisions to console
    MaxIterations = 25     // safety limit on agent loop
};
```

## NuGet packages

| Package | Purpose |
|---|---|
| `Anthropic` v12+ | Official Anthropic C# SDK |
| `Microsoft.Playwright` | Browser automation |
