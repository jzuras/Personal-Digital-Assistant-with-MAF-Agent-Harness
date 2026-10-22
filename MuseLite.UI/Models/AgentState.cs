using System.Text.Json.Serialization;

namespace MuseLite.UI.Models;

/// <summary>
/// The predictive state sent by the agentic_generative_ui agent.
/// Maps to the Plan/steps structure the server emits via STATE_SNAPSHOT events.
/// </summary>
public sealed class AgentState
{
    /// <summary>Gets or sets the list of execution steps.</summary>
    [JsonPropertyName("steps")]
    public List<TaskStep> Steps { get; set; } = [];
}

/// <summary>
/// A single step in the agent's execution plan.
/// </summary>
public sealed class TaskStep
{
    /// <summary>Gets or sets the human-readable step description.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    /// <summary>Gets or sets the step's current status.</summary>
    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter<StepStatus>))]
    public StepStatus Status { get; set; } = StepStatus.Pending;
}

/// <summary>
/// Status values for a task step. Values are lowercase to match the AG-UI wire format.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<StepStatus>))]
public enum StepStatus
{
    /// <summary>Step has not started yet.</summary>
    Pending,

    /// <summary>Step has been completed.</summary>
    Completed,
}
