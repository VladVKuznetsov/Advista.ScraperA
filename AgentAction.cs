namespace AgentScraper.Models;

public enum AgentActionType
{
    Click,
    Extract,
    Done,
    Scroll
}

public record AgentAction(
    AgentActionType Type,
    string? Selector = null,       // CSS selector or accessibility label
    string? Reason = null,         // Claude's reasoning (useful for debug)
    List<Department>? Data = null  // populated when Type == Extract or Done
);
