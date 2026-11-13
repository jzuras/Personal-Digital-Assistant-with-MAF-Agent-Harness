using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace MyAssistant.DurableContext;

/// <summary>
/// The result of the <c>show_images</c> tool: the images to display. An object wrapper (not a bare array) so
/// a consumer can recognize an image result by its <c>images</c> property. A shared serialization contract
/// between the server's tool and <see cref="DurableHistoryReader"/>.
/// </summary>
[SuppressMessage(
    "Usage",
    "CA2227:Collection properties should be read only",
    Justification = "JSON DTO — the images collection needs a public setter for System.Text.Json (de)serialization.")]
[SuppressMessage(
    "Design",
    "CA1002:Do not expose generic lists",
    Justification = "JSON DTO — List<T> is the System.Text.Json (de)serialization shape for the images array.")]
public sealed class ImageResult
{
    /// <summary>Gets or sets the images to display.</summary>
    [JsonPropertyName("images")]
    public List<ImageItem> Images { get; set; } = [];
}
