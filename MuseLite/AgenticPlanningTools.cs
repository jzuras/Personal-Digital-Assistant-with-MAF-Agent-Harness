using System.ComponentModel;

namespace MyAssistantServer.AgenticUI;

/// <summary>
/// Agentic-UI planning tools. The model calls these to drive the client's Task Progress card: the data
/// they return <em>is</em> the UI state (a full snapshot from <see cref="CreatePlan"/>, incremental deltas
/// from <see cref="UpdatePlanStepAsync"/>). The parameter/return shapes and the lowercase status wire format
/// are a contract the client mirrors, so they must not drift. See the Agentic UI guide for the full pattern.
/// </summary>
internal static class AgenticPlanningTools
{
    /// <summary>
    /// Creates a plan from a list of step descriptions, all initially <see cref="StepStatus.Pending"/>.
    /// The returned <see cref="Plan"/> is sent to the client as the initial state snapshot.
    /// </summary>
    /// <param name="steps">The ordered step descriptions to turn into plan steps.</param>
    /// <returns>A new plan with one pending step per description.</returns>
    [Description("Create a plan with multiple steps.")]
    public static Plan CreatePlan(
        [Description("List of step descriptions to create the plan.")] List<string> steps)
    {
        return new Plan
        {
            Steps = [.. steps.Select(s => new Step { Description = s, Status = StepStatus.Pending })],
        };
    }

    /// <summary>
    /// Updates one step's description and/or status, returning the change as RFC 6902 JSON Patch operations
    /// (an incremental state delta the client applies to its mirrored plan). A short delay makes the
    /// step-by-step progression visible in the demo UI.
    /// </summary>
    /// <param name="index">The zero-based index of the step to update.</param>
    /// <param name="description">The new description, or <see langword="null"/> to leave it unchanged.</param>
    /// <param name="status">The new status, or <see langword="null"/> to leave it unchanged.</param>
    /// <returns>The patch operations describing the change (empty if nothing was supplied).</returns>
    [Description("Update a step in the plan with new description or status.")]
    public static async Task<List<JsonPatchOperation>> UpdatePlanStepAsync(
        [Description("The index of the step to update.")] int index,
        [Description("The new description for the step (optional).")] string? description = null,
        [Description("The new status for the step (optional).")] StepStatus? status = null)
    {
        var changes = new List<JsonPatchOperation>();

        if (description is not null)
        {
            changes.Add(new JsonPatchOperation
            {
                Op = "replace",
                Path = $"/steps/{index}/description",
                Value = description,
            });
        }

        if (status.HasValue is true)
        {
            // Status goes over the wire lowercase ("pending"/"completed") to match the client's mirror.
            string statusValue = (status.Value == StepStatus.Pending) ? "pending" : "completed";
            changes.Add(new JsonPatchOperation
            {
                Op = "replace",
                Path = $"/steps/{index}/status",
                Value = statusValue,
            });
        }

        // Deliberate pause so the UI animates one step at a time rather than snapping to done (demo only).
        await Task.Delay(1000);

        return changes;
    }
}
