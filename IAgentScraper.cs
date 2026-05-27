using AgentScraper.Models;

namespace AgentScraper;

public interface IAgentScraper
{
    /// <summary>
    /// Navigates to <paramref name="url"/>, uses Claude to understand the page,
    /// performs any necessary clicks/scrolls, and returns all extracted departments.
    /// </summary>
    Task<List<Department>> ExtractAsync(
        string url,
        string prompt,
        CancellationToken ct = default);
}
