using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace MyAssistantServer.AgenticUI;

/// <summary>
/// One image supplied to the <c>show_images</c> tool for the client to render inline. The client mirrors
/// this shape; only <see cref="Url"/> is required, the rest improve the presentation.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Instantiated by System.Text.Json when binding the show_images tool arguments.")]
internal sealed class ImageItem
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
