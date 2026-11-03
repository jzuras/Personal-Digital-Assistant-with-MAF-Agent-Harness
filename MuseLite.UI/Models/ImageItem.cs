using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace MuseLite.UI.Models;

/// <summary>
/// One image to display in the inline gallery, mirroring the server's <c>show_images</c> shape.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1056:URI-like properties should not be strings",
    Justification = "URLs arrive as arbitrary JSON strings from the model/web and are only ever rendered " +
        "into an <img src>/<a href>. Keeping them as string lets a malformed value show as a broken image " +
        "rather than throw during deserialization, which is deliberate here.")]
public sealed class ImageItem
{
    /// <summary>Gets or sets the direct URL of the image.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    /// <summary>Gets or sets alternative text describing the image, for accessibility.</summary>
    [JsonPropertyName("alt")]
    public string? Alt { get; set; }

    /// <summary>Gets or sets an optional caption shown beneath the image.</summary>
    [JsonPropertyName("caption")]
    public string? Caption { get; set; }

    /// <summary>Gets or sets the URL of the page the image came from, for attribution.</summary>
    [JsonPropertyName("sourceUrl")]
    public string? SourceUrl { get; set; }
}
