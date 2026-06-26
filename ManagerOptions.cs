namespace Advista.Tasks._180no.CrawlerLocators.Components.Anthropic.Services;

/// <summary>Options for the overall extraction process, bound from the "ManagerOptions" appsettings section.</summary>
public sealed class ManagerOptions
{
    /// <summary>When true, the extraction chain logs its progress via the injected logger.</summary>
    public bool VerboseChainProcess { get; set; }
}
