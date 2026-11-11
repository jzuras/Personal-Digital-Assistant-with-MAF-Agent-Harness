using System.Text.Json.Serialization;

namespace MyAssistantServer.AgenticUI;

/// <summary>
/// The full plan state produced by the <c>create_plan</c> tool and sent to the client as a state
/// snapshot. The client mirrors this shape (property name <c>steps</c>) to render its Task Progress card.
/// </summary>
internal sealed class Plan
{
    /// <summary>Gets or sets the ordered list of steps in the plan.</summary>
    [JsonPropertyName("steps")]
    public List<Step> Steps { get; set; } = [];
}
