using System.Text.Json.Serialization;

namespace MyAssistantServer.AgenticUI;

/// <summary>
/// Status of a plan <see cref="Step"/>. Serialized lowercase (<c>"pending"</c> / <c>"completed"</c>) to
/// match the AG-UI wire format the client expects; the <c>update_plan_step</c> tool also lowercases the
/// value it writes into its JSON Patch.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<StepStatus>))]
internal enum StepStatus
{
    /// <summary>The step has not been completed yet.</summary>
    Pending,

    /// <summary>The step has been completed.</summary>
    Completed,
}
