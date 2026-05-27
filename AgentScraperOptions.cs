namespace AgentScraper;

public sealed class AgentScraperOptions
{
    /// <summary>Max agent loop iterations before giving up.</summary>
    public int MaxIterations { get; init; } = 20;

    /// <summary>Run browser in headless mode. Set false to watch it work.</summary>
    public bool Headless { get; init; } = true;

    /// <summary>Print debug output to console.</summary>
    public bool Verbose { get; init; } = true;

    /// <summary>Navigation timeout in ms.</summary>
    public float NavigationTimeoutMs { get; init; } = 30_000;
}
