using System.Text;
using System.Text.Json;

namespace MyAssistant.ApprovalPolicy;

/// <summary>
/// Produces a stable, canonical string for a tool call's arguments so that "the same
/// arguments" compare equal across runs regardless of key ordering. Object keys are sorted
/// recursively (ordinal) and the result is emitted as compact JSON. This is the exact-args
/// matching key for <see cref="ApprovalRuleStore"/>.
/// </summary>
public static class ArgCanonicalizer
{
    /// <summary>
    /// Canonicalizes a tool call's arguments into a stable string.
    /// </summary>
    /// <param name="arguments">The tool call arguments (e.g. <c>FunctionCallContent.Arguments</c>),
    /// or <see langword="null"/> when the call has none.</param>
    /// <returns>A canonical JSON string with object keys sorted recursively. Returns
    /// <c>"{}"</c> for null or empty arguments.</returns>
    public static string Canonicalize(IEnumerable<KeyValuePair<string, object?>>? arguments)
    {
        if (arguments is null)
        {
            return "{}";
        }

        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in arguments)
        {
            map[pair.Key] = pair.Value;
        }

        if (map.Count == 0)
        {
            return "{}";
        }

        using JsonDocument document = JsonSerializer.SerializeToDocument(map);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            ArgCanonicalizer.WriteCanonical(document.RootElement, writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Recursively writes <paramref name="element"/> to <paramref name="writer"/> with object
    /// keys sorted ordinally. Array order is preserved; scalar values are written as-is.
    /// </summary>
    /// <param name="element">The JSON element to write.</param>
    /// <param name="writer">The writer that receives the canonical form.</param>
    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    ArgCanonicalizer.WriteCanonical(property.Value, writer);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    ArgCanonicalizer.WriteCanonical(item, writer);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }
}
