using System.Text.Json.Serialization;

namespace MyAssistantServer.AgenticUI;

/// <summary>
/// The result of the <c>show_images</c> tool: the images to display. An object wrapper (rather than a bare
/// array) so the client can tell an image result apart from other tool results by its <c>images</c>
/// property — the same way the plan snapshot is recognized by its <c>steps</c> property.
/// </summary>
internal sealed class ImageResult
{
    /// <summary>Gets or sets the images to display.</summary>
    [JsonPropertyName("images")]
    public List<ImageItem> Images { get; set; } = [];
}
