using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Advista.Tasks._180no.CrawlerLocators.Components.Anthropic.Services;

/// <summary>
/// Top-level entry point. Owns the <see cref="AnthropicBrowserOptions"/> configuration and runs a single
/// extraction, returning <c>(departments, parseError)</c> so callers get a displayable error string
/// instead of a thrown exception.
/// </summary>
public sealed class AnthropicBrowserExtractor : IAnthropicBrowserExtractor
{
    private readonly IOptions<AnthropicBrowserOptions> _anthropicOptions;
    private readonly IOptions<ManagerOptions> _managerOptions;
    private readonly ILogger<AnthropicBrowserExtractor> _logger;

    // The extraction goal is an implementation detail of this extractor — callers don't supply it.
    private const string Prompt =
        "Find every department/branch/store and open its detail page. " +
        "For each, extract: title, addressLine, addressCity, addressZip, telephone, email.";

    public AnthropicBrowserExtractor(
        IOptions<AnthropicBrowserOptions> anthropicOptions,
        IOptions<ManagerOptions> managerOptions,
        ILogger<AnthropicBrowserExtractor> logger)
    {
        _anthropicOptions = anthropicOptions;
        _managerOptions = managerOptions;
        _logger = logger;
    }

    /// <summary>
    /// Extracts all departments from <paramref name="url"/>.
    /// </summary>
    /// <returns>
    /// <c>departments</c>: the extracted records (empty list, never null).
    /// <c>parseError</c>: empty when everything succeeded; otherwise a message describing why the
    /// departments could not be extracted (timeout, Anthropic/API failure, parse error, etc.).
    /// </returns>
    public async Task<(List<DepartmentAnthAuto> departments, string parseError)> ExtractAsync(
        string url,
        string? tips = null,
        string defaultCountry = "NO",
        CancellationToken ct = default)
    {
        try
        {
            // The Anthropic client and the Playwright scraper are created here, not in the constructor:
            //  - a constructor cannot be async, and the headless browser is created/awaited;
            //  - the browser is a heavy disposable best scoped to one run — `await using` guarantees
            //    it is torn down when this method returns, success or failure;
            //  - any setup failure then surfaces through parseError below rather than at construction.
            var options = _anthropicOptions.Value;

            EnsureBrowsersInstalled(options);

            var claude = new AnthropicClient(new ClientOptions
            {
                ApiKey = options.ApiKey,
                MaxRetries = options.MaxRetries,
                Timeout = TimeSpan.FromMinutes(options.HttpTimeoutMinutes),
            });

            await using var scraper = new PlaywrightAgentScraper(claude, options, _managerOptions, _logger);

            var departments = await scraper.ExtractAsync(url, Prompt, tips, defaultCountry, ct);

            if (departments.Count == 0)
                return (new List<DepartmentAnthAuto>(), $"No departments could be extracted from {url}. Check the URL and tips.");

            return (departments, string.Empty);
        }
        catch (Exception ex)
        {
            return (new List<DepartmentAnthAuto>(), $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static int _browsersInstalled; // 0 = not yet attempted this process

    /// <summary>
    /// Installs the Chromium browser binaries once per process (if <see cref="AnthropicBrowserOptions.AutoInstallBrowsers"/>).
    /// Requires the Playwright driver to already be present; a missing driver throws here and is
    /// surfaced as parseError by the caller.
    /// </summary>
    private void EnsureBrowsersInstalled(AnthropicBrowserOptions options)
    {
        if (!options.AutoInstallBrowsers) return;
        if (Interlocked.Exchange(ref _browsersInstalled, 1) != 0) return; // already done this process

        if (_managerOptions.Value.VerboseChainProcess)
            _logger.LogInformation("Ensuring Playwright browser (chromium) is installed…");

        var exitCode = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
        if (exitCode != 0)
            throw new InvalidOperationException(
                $"Playwright 'install chromium' failed (exit {exitCode}). Is the linux-x64 driver deployed?");
    }
}
