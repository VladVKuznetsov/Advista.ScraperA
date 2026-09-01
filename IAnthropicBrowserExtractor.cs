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
    /// <param name="expandSelector">
    /// Optional CSS selector for collapsible sections (accordion headers) to expand before
    /// extraction. Every matching element is clicked. Null = nothing to expand.
    /// </param>
    /// <param name="inlinePage">
    /// When true, all departments live on this single page (no per-department subpages): the whole
    /// page is read and a list of departments is extracted from it, instead of discovering links.
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
        string? expandSelector = null,
        bool inlinePage = false,
        CancellationToken ct = default);
}
