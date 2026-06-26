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
    /// <returns>
    /// <c>departments</c>: the extracted records (empty list, never null).
    /// <c>parseError</c>: empty when everything succeeded; otherwise a message describing why the
    /// departments could not be extracted (timeout, Anthropic/API failure, parse error, etc.).
    /// </returns>
    Task<(List<DepartmentAnthAuto> departments, string parseError)> ExtractAsync(
        string url,
        string? tips = null,
        string defaultCountry = "NO",
        CancellationToken ct = default);
}
