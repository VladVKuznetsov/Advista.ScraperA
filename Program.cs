using System.Text.Encodings.Web;
using System.Text.Json;
using Advista.Tasks._180no.CrawlerLocators.Components.Anthropic.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;

// ── Browser install ─────────────────────────────────────────────────────────────
// OS-agnostic way to download the Playwright browser binaries (no PowerShell needed):
//   dotnet AgentScraper.dll --install-browsers              (browser only)
//   sudo dotnet AgentScraper.dll --install-browsers --with-deps   (browser + OS libraries)
if (args.Contains("--install-browsers"))
    return Microsoft.Playwright.Program.Main(
        args.Contains("--with-deps")
            ? new[] { "install", "--with-deps", "chromium" }
            : new[] { "install", "chromium" });

// ── Config ────────────────────────────────────────────────────────────────────
// Layered so the gitignored appsettings.json (real ApiKey) wins, while an environment file can
// still contribute keys it alone defines (e.g. the Linux browser paths):
//   1. appsettings.development.json      committed template (placeholder ApiKey)
//   2. appsettings.{ENVIRONMENT}.json     e.g. appsettings.Production.Linux.json (set DOTNET_ENVIRONMENT)
//   3. appsettings.json                   local/secret values — wins on overlapping keys
var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                  ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

var configBuilder = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.development.json", optional: true);

if (!string.IsNullOrWhiteSpace(environment))
    configBuilder.AddJsonFile($"appsettings.{environment}.json", optional: true);

var config = configBuilder
    .AddJsonFile("appsettings.json", optional: true)
    .Build();

var anthropicOptions = config.GetSection("AnthropicBrowserOptions").Get<AnthropicBrowserOptions>()
    ?? throw new InvalidOperationException("AnthropicBrowserOptions section missing from appsettings.");

if (string.IsNullOrWhiteSpace(anthropicOptions.ApiKey) || anthropicOptions.ApiKey == "YOUR_ANTHROPIC_API_KEY_HERE")
    throw new InvalidOperationException("AnthropicBrowserOptions.ApiKey not set — copy appsettings.development.json to appsettings.json and add your key.");

var managerOptions = config.GetSection("ManagerOptions").Get<ManagerOptions>() ?? new ManagerOptions();

// ── Logging (Serilog) ───────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

using var loggerFactory = LoggerFactory.Create(b => b.AddSerilog(Log.Logger, dispose: true));

var extractor = new AnthropicBrowserExtractor(
    Options.Create(anthropicOptions),
    Options.Create(managerOptions),
    loggerFactory.CreateLogger<AnthropicBrowserExtractor>());

// Human tips: tell the agent where the department list lives and which element to follow.
// The model matches these against the real DOM candidates (each candidate exposes its href,
// visible text and CSS classes), so the most useful hints are an href pattern and class tokens.

// openListSelector: clicked once after load to reveal a list hidden behind a trigger (biltema). Null otherwise.
string? openListSelector = null;

// ── heidenreich.no (JS-navigation <li>s, no href — agent clicks them) ──
// var url = "https://www.heidenreich.no/butikker";
// var tips = """
//     The department list is a <ul> whose class looks like:
//       "[&_>_li]:break-inside_avoid column-count_1 lg:column-count_2 cg_5xl"
//     Each department is an <li> (class like
//       "d_flex ai_center jc_space-between py_md bd-b_1px_solid_{colors.border} focus:ring-c_primary cursor_pointer")
//     showing the store name and opening hours. Follow every such <li> to open its detail subpage,
//     and read the department data there.
//     """;

// ── xl-bygg.no (real <a> links with href) ──
// var url = "https://www.xl-bygg.no/butikker";
// var tips = """
//     The store/department links are <a> anchors whose href looks like "/butikker/xl-bygg-<store>"
//     (for example "/butikker/xl-bygg-alna"). Each such anchor's visible text is the store name, and
//     its class contains tokens like "jc_space-between", "ai_start", "py_16s", "bd-b_1px_solid" and
//     "anim_tabEnter". Select every anchor whose href matches that "/butikker/xl-bygg-..." pattern —
//     each opens one store's detail page. Ignore navigation, language, social and footer links.
//     """;

// ── biltema.no (store list hidden in a drawer opened by clicking #react__storeselector) ──
var url = "https://www.biltema.no/";
openListSelector = "#react__storeselector";
var tips = """
    The store/department links are <a> anchors whose href looks like
    "https://www.biltema.no/varehus/<store>/" (for example "https://www.biltema.no/varehus/alta/").
    They live in a side drawer (a <ul class="storeselector">) that opens after the page loads. Each
    anchor's visible text is the store/city name. Select every anchor whose href matches the
    "/varehus/<store>/" pattern — each opens one store's detail page. The same store may appear more
    than once; duplicates are fine. Ignore navigation, language, social and footer links.
    """;

var (departments, parseError) = await extractor.ExtractAsync(
    url: url,
    tips: tips,
    openListSelector: openListSelector
);

// ── Output ────────────────────────────────────────────────────────────────────
if (!string.IsNullOrEmpty(parseError))
{
    Console.WriteLine($"\n❌ Extraction failed: {parseError}");
    return 1;
}

Console.WriteLine($"\n✅ Extracted {departments.Count} departments\n");

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep æ/ø/å and + literal, not \uXXXX
};
var json = JsonSerializer.Serialize(departments, jsonOptions);
Console.WriteLine(json);

await File.WriteAllTextAsync("departments.json", json);
Console.WriteLine("\nSaved to departments.json");

return 0;
