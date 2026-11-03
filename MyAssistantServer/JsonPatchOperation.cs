using System.Text.Json.Serialization;

namespace MyAssistantServer.AgenticUI;

/// <summary>
/// A single RFC 6902 JSON Patch operation emitted by the <c>update_plan_step</c> tool as an incremental
/// state delta. The client applies <c>replace</c> ops on the paths <c>/steps/{index}/status</c> and
/// <c>/steps/{index}/description</c> to its mirrored plan state.
/// </summary>
internal sealed class JsonPatchOperation
{
    /// <summary>Gets or sets the operation type (always <c>"replace"</c> for the plan tools).</summary>
    [JsonPropertyName("op")]
    public required string Op { get; set; }

    /// <summary>Gets or sets the JSON Pointer path to the value being replaced.</summary>
    [JsonPropertyName("path")]
    public required string Path { get; set; }

    /// <summary>Gets or sets the replacement value.</summary>
    [JsonPropertyName("value")]
    public object? Value { get; set; }

    /// <summary>Gets or sets the source path (used by <c>move</c>/<c>copy</c> ops; unused by the plan tools).</summary>
    [JsonPropertyName("from")]
    public string? From { get; set; }
}
