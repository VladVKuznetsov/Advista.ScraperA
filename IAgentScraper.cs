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
    Task<List<DepartmentAnthAuto>> ExtractAsync(
        string url,
        string prompt,
        string? tips = null,
        string defaultCountry = "NO",
        CancellationToken ct = default);
}
