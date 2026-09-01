namespace Advista.Tasks._180no.CrawlerLocators.Components.Anthropic.Services;

public interface IAgentScraper
{
    /// <summary>
    /// Navigates to <paramref name="url"/>, uses Claude (guided by the human <paramref name="tips"/>)
    /// to find the per-department links, visits each one, extracts its contact record, and returns
    /// all departments after filtering out site-wide repeated (footer / head-office) records.
    /// </summary>
    /// <param name="tips">
    /// Human hints describing where the department list lives and which element to follow
    /// (e.g. which UL / LI / anchor, by class or text). Helps the model locate the right click path.
    /// </param>
    /// <param name="defaultCountry">
    /// ISO region code used to format phone numbers to E.164 when they carry no international
    /// trunk prefix (e.g. "NO" for Norway).
    /// </param>
    /// <param name="openListSelector">
    /// Optional CSS selector clicked once after load to reveal a list hidden behind a trigger
    /// (e.g. a drawer/modal opener). Null = no pre-click.
    /// </param>
    /// <param name="expandSelector">
    /// Optional CSS selector for collapsible sections (accordion headers) to expand before
    /// extraction. Every matching element is clicked. Null = nothing to expand.
    /// </param>
    /// <param name="inlinePage">
    /// When true, all departments live on this single page (no per-department subpages): the whole
    /// page is read and a list of departments is extracted from it, instead of discovering links.
    /// </param>
    Task<List<DepartmentAnthAuto>> ExtractAsync(
        string url,
        string prompt,
        string? tips = null,
        string defaultCountry = "NO",
        string? openListSelector = null,
        string? expandSelector = null,
        bool inlinePage = false,
        CancellationToken ct = default);
}
