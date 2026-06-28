namespace Advista.Tasks._180no.CrawlerLocators.Components.Anthropic.Services;

/// <summary>
/// Runs a single department extraction, returning <c>(departments, parseError)</c> so callers get a
/// displayable error string instead of a thrown exception.
/// </summary>
public interface IAnthropicBrowserExtractor
{
    /// <summary>
    /// Extracts all departments from <paramref name="url"/>.
    /// </summary>
    /// <param name="openListSelector">
    /// Optional CSS selector to click once after the page loads, before discovery — used when the
    /// store list is hidden behind a trigger (e.g. a drawer/modal opener). Null = no pre-click.
    /// </param>
    /// <returns>
    /// <c>departments</c>: the extracted records (empty list, never null).
    /// <c>parseError</c>: empty when everything succeeded; otherwise a message describing why the
    /// departments could not be extracted (timeout, Anthropic/API failure, parse error, etc.).
    /// </returns>
    Task<(List<DepartmentAnthAuto> departments, string parseError)> ExtractAsync(
        string url,
        string? tips = null,
        string defaultCountry = "NO",
        string? openListSelector = null,
        CancellationToken ct = default);
}
