using System.Text.Json.Serialization;

namespace MyAssistant.DurableContext;

/// <summary>
/// Minimal display projection of one stored conversation turn, returned by <see cref="DurableHistoryReader"/>
/// for client hydration. The chat UI renders user and assistant text turns, plus any inline image gallery the
/// <c>show_images</c> tool produced for an assistant answer (reconstructed from the stored tool result so a
/// reopened client shows the pictures, not just the text). Serialized as camelCase
/// (<c>{role, text, images?}</c>); <see cref="Images"/> is omitted when the turn has none.
/// </summary>
/// <param name="Role">The turn's role — <c>"user"</c> or <c>"assistant"</c>.</param>
/// <param name="Text">The turn's text.</param>
/// <param name="Images">The inline image gallery for an assistant turn, or <see langword="null"/>.</param>
public sealed record HistoryMessage(
    string Role,
    string Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ImageResult? Images = null);
