namespace Advista.Tasks._180no.CrawlerLocators.Components.Anthropic.Services;

/// <summary>
/// All scraper + Anthropic configuration, bound from the "AnthropicBrowserOptions" appsettings section.
/// </summary>
public sealed class AnthropicBrowserOptions
{
    // ── Anthropic client / request ────────────────────────────────────────────

    /// <summary>Anthropic API key (X-Api-Key).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Model id used for every request.</summary>
    public string Model { get; set; } = "claude-sonnet-4-6";

    /// <summary>Max tokens to generate per request.</summary>
    public int MaxTokens { get; set; } = 2048;

    /// <summary>Max automatic retries on transient failures (exponential backoff).</summary>
    public int MaxRetries { get; set; } = 4;

    /// <summary>Sampling temperature (0 = deterministic).</summary>
    public double Temperature { get; set; }

    /// <summary>When true, requests use the streaming endpoint (text is accumulated).</summary>
    public bool Stream { get; set; }

    /// <summary>Per-request HTTP timeout in minutes (does not include retries).</summary>
    public int HttpTimeoutMinutes { get; set; } = 10;

    // ── Browser / scraping ────────────────────────────────────────────────────

    /// <summary>Run browser in headless mode. Set false to watch it work.</summary>
    public bool Headless { get; set; } = true;

    /// <summary>
    /// When true, the Chromium browser binaries are installed on first use if missing.
    /// The Playwright driver itself must already be deployed (a linux-x64 publish) — only the
    /// browser is installed here, not the driver or OS-level library dependencies.
    /// </summary>
    public bool AutoInstallBrowsers { get; set; } = true;

    /// <summary>Navigation timeout in ms.</summary>
    public float NavigationTimeoutMs { get; set; } = 30_000;

    /// <summary>Safety cap on how many department detail pages to visit.</summary>
    public int MaxDepartments { get; set; } = 300;

    // ── Call logging ──────────────────────────────────────────────────────────

    /// <summary>When true, every Claude API call (count + prompts + response) is appended to <see cref="AnthropicAutomationCallLogPath"/>.</summary>
    public bool AnthropicAutomationCallLog { get; set; }

    /// <summary>File to append the Claude call log to (used only when <see cref="AnthropicAutomationCallLog"/> is true).</summary>
    public string? AnthropicAutomationCallLogPath { get; set; }

    /// <summary>Custom browser executable for Puppeteer-based tools. Empty = use the default browser.</summary>
    public string PuppeteerBrowserExecutablePath { get; set; } = string.Empty;

    /// <summary>Custom browser executable for Playwright. Empty = use Playwright's default (bundled) browser.</summary>
    public string PlaywrightBrowserExecutablePath { get; set; } = string.Empty;
}
