using System.Text.Json;
using System.Text.Json.Serialization;

namespace MuseLite.UI.Models;

/// <summary>
/// Represents a single RFC 6902 JSON Patch operation as received from the agent's STATE_DELTA events.
/// </summary>
public sealed class JsonPatchOperation
{
    /// <summary>Gets or sets the operation type (e.g. "replace").</summary>
    [JsonPropertyName("op")]
    public string Op { get; set; } = "";

    /// <summary>Gets or sets the JSON Pointer path to the target location.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>Gets or sets the value to apply at the target location.</summary>
    [JsonPropertyName("value")]
    public JsonElement Value { get; set; }

    /// <summary>Gets or sets the source path (used by "move" and "copy" operations).</summary>
    [JsonPropertyName("from")]
    public string? From { get; set; }
}
