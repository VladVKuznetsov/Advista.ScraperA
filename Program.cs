using System.Text.Json;
using AgentScraper;
using Anthropic;

// ── Config ────────────────────────────────────────────────────────────────────
var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(
    await File.ReadAllTextAsync("appsettings.json"))
    ?? throw new InvalidOperationException("Could not parse appsettings.json.");

var apiKey = settings.TryGetValue("ANTHROPIC_API_KEY", out var k) && !string.IsNullOrEmpty(k)
    ? k
    : throw new InvalidOperationException("ANTHROPIC_API_KEY not set in appsettings.json.");

Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", apiKey);
var claude = new AnthropicClient();

var options = new AgentScraperOptions
{
    Headless = false,   // set false to watch the browser
    Verbose  = true,
    MaxIterations = 25
};

// ── Run ───────────────────────────────────────────────────────────────────────
await using var scraper = new PlaywrightAgentScraper(claude, options);

var departments = await scraper.ExtractAsync(
    url: "https://www.bilglass.no/avdeling",
    prompt: "Find all department/branch offices. " +
            "If there are region or county filters, click each one to reveal its departments. " +
            "For every department extract: title, addressLine, addressCity, addressZip, telephone, email."
);

// ── Output ────────────────────────────────────────────────────────────────────
Console.WriteLine($"\n✅ Extracted {departments.Count} departments\n");

var json = JsonSerializer.Serialize(departments, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(json);

await File.WriteAllTextAsync("departments.json", json);
Console.WriteLine("\nSaved to departments.json");
