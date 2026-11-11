using System.ComponentModel;

namespace MyAssistantServer.AgenticUI;

/// <summary>
/// Agentic-UI tool for showing images inline. The model calls <see cref="ShowImages"/> with image URLs it
/// found (for example, via web search); the data it returns IS the content the client renders as a gallery
/// attached to the answer. Un-gated: it only drives display and has no side effects.
/// </summary>
internal static class ImageTools
{
    private static string[] FilePageMarkers { get; } = ["/wiki/File:", "/wiki/Image:"];

    /// <summary>
    /// Displays one or more images to the user inline, alongside the assistant's text answer.
    /// </summary>
    /// <param name="images">The images to show.</param>
    /// <returns>The images wrapped for the client to render.</returns>
    [Description(
        "Show the user one or more images inline (for example, photos found with web search). For each "
        + "image provide a direct image URL ending in .jpg, .png, .webp, or .gif (NOT a web page URL), a "
        + "short caption, and the page it came from. Use only real image URLs you actually found; never "
        + "invent one.")]
    public static ImageResult ShowImages(
        [Description("The images to display.")] List<ImageItem> images)
    {
        foreach (var image in images)
        {
            // Wikipedia/Wikimedia web search returns the File: *description page*, not a direct image URL.
            // Models routinely can't derive the real upload.wikimedia.org path (its sharded prefix is a
            // content hash) and fabricate one, which 404s. When the source is a File: page we can build a
            // guaranteed-direct URL deterministically via Special:FilePath, which redirects to the real
            // file — so trust that over whatever the model put in Url.
            if (ResolveWikimediaDirectImage(image.SourceUrl) is { } resolved)
            {
                image.Url = resolved;
            }
        }

        return new ImageResult { Images = images };
    }

    /// <summary>
    /// If <paramref name="sourceUrl"/> is a Wikipedia/Wikimedia <c>File:</c> (or legacy <c>Image:</c>)
    /// description page, returns a direct image URL built from <c>Special:FilePath</c> on the same host,
    /// which redirects to the real underlying file. Returns <see langword="null"/> for anything else, so
    /// non-Wikimedia sources are left untouched.
    /// </summary>
    private static string? ResolveWikimediaDirectImage(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl)
            || Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) is false
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        var host = uri.Host;
        var isWikimediaHost =
            host.EndsWith(".wikipedia.org", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".wikimedia.org", StringComparison.OrdinalIgnoreCase)
            || host.Equals("wikipedia.org", StringComparison.OrdinalIgnoreCase)
            || host.Equals("wikimedia.org", StringComparison.OrdinalIgnoreCase);
        if (isWikimediaHost is false)
        {
            return null;
        }

        // AbsolutePath keeps the original percent-encoding, so the filename segment can be reattached to
        // Special:FilePath as-is. Match the File:/Image: namespace case-insensitively.
        var path = uri.AbsolutePath;
        foreach (var marker in FilePageMarkers)
        {
            var idx = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                continue;
            }

            var fileName = path[(idx + marker.Length)..];
            if (fileName.Length == 0)
            {
                return null;
            }

            return $"https://{host}/wiki/Special:FilePath/{fileName}";
        }

        return null;
    }
}
