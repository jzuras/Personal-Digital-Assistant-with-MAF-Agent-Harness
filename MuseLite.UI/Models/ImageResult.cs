using System.Text.Json.Serialization;

namespace MuseLite.UI.Models;

/// <summary>
/// The <c>show_images</c> tool result: the set of images to render as an inline gallery. Recognized on the
/// wire by its <c>images</c> property, the same way the plan snapshot is recognized by <c>steps</c>.
/// </summary>
public sealed class ImageResult
{
    /// <summary>Gets or sets the images to display.</summary>
    [JsonPropertyName("images")]
    public List<ImageItem> Images { get; set; } = [];
}
