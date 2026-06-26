namespace Advista.Tasks._180no.CrawlerLocators.Components.Anthropic.Services;

/// <summary>One department / branch / store contact record extracted by the Anthropic automation.</summary>
public class DepartmentAnthAuto
{
    public string Title { get; set; } = string.Empty;

    /// <summary>Combined address: "AddressLine, AddressZip AddressCity".</summary>
    public string FullAddress { get; set; } = string.Empty;

    public string AddressLine { get; set; } = string.Empty;
    public string AddressZip { get; set; } = string.Empty;
    public string AddressCity { get; set; } = string.Empty;

    /// <summary>Phone in E.164 format (e.g. +4755538600).</summary>
    public string Telephone { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    /// <summary>URL of the department detail page this record was read from.</summary>
    public string Url { get; set; } = string.Empty;
}
