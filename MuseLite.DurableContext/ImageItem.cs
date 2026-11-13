using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace MyAssistant.DurableContext;

/// <summary>
/// One image from the <c>show_images</c> tool, for the client to render inline. Only <see cref="Url"/> is
/// required; the rest improve presentation. A shared serialization contract: the server's tool produces it,
/// and <see cref="DurableHistoryReader"/> reconstructs it from the stored tool result during hydration.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1056:URI-like properties should not be strings",
    Justification = "JSON serialization contract — image URLs are carried verbatim as model-provided strings, not validated System.Uri.")]
public sealed class ImageItem
{
    /// <summary>Gets or sets the direct URL of the image (ending in .jpg/.png/.webp/.gif, not a web page).</summary>
    [JsonPropertyName("url")]
    public required string Url { get; set; }

    /// <summary>Gets or sets short alternative text describing the image, for accessibility.</summary>
    [JsonPropertyName("alt")]
    public string? Alt { get; set; }

    /// <summary>Gets or sets an optional caption shown beneath the image.</summary>
    [JsonPropertyName("caption")]
    public string? Caption { get; set; }

    /// <summary>Gets or sets the URL of the page the image came from, for attribution.</summary>
    [JsonPropertyName("sourceUrl")]
    public string? SourceUrl { get; set; }
}
