using System.Text.Encodings.Web;
using System.Text.Json;
using Advista.Tasks._180no.CrawlerLocators.Components.Anthropic.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;

// ── Config ────────────────────────────────────────────────────────────────────
// appsettings.development.json is the committed template (placeholder ApiKey); appsettings.json
// holds the real key and is gitignored. It is layered on top, so its values win where they overlap.
var config = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.development.json", optional: true)
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
// The model matches these against the real DOM candidates (the items are JS-navigation <li>s
// with no href, so the agent clicks them and captures the resulting URL).
var tips = """
    The department list is a <ul> whose class looks like:
      "[&_>_li]:break-inside_avoid column-count_1 lg:column-count_2 cg_5xl"
    Each department is an <li> (class like
      "d_flex ai_center jc_space-between py_md bd-b_1px_solid_{colors.border} focus:ring-c_primary cursor_pointer")
    showing the store name and opening hours. Follow every such <li> to open its detail subpage,
    and read the department data there.
    """;

var (departments, parseError) = await extractor.ExtractAsync(
    url: "https://www.heidenreich.no/butikker",
    tips: tips
);

// ── Output ────────────────────────────────────────────────────────────────────
if (!string.IsNullOrEmpty(parseError))
{
    Console.WriteLine($"\n❌ Extraction failed: {parseError}");
    return;
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
