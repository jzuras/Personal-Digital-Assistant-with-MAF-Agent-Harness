using System.Text.Json.Serialization;

namespace MyAssistantServer.AgenticUI;

/// <summary>
/// One step in a <see cref="Plan"/>. The client mirrors this shape (property names <c>description</c> and
/// <c>status</c>) to render each row of its Task Progress card.
/// </summary>
internal sealed class Step
{
    /// <summary>Gets or sets the human-readable description of the step.</summary>
    [JsonPropertyName("description")]
    public required string Description { get; set; }

    /// <summary>Gets or sets the step's current status. Defaults to <see cref="StepStatus.Pending"/>.</summary>
    [JsonPropertyName("status")]
    public StepStatus Status { get; set; } = StepStatus.Pending;
}
