using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MyAssistant.DurableContext;

/// <summary>
/// Reads the one durable context's stored transcript from a snapshot and projects it to a minimal
/// <c>{role, text, images}</c> shape for client hydration — <strong>without a model</strong>.
/// </summary>
/// <remarks>
/// <para>
/// Deserializing a session and reading its history are pure deserialization/read operations, not model
/// calls, so this reader drives them through a throwaway <em>dummy</em> <see cref="IChatClient"/> (no model,
/// no API key). Only the history provider is needed to recover the transcript slice — verified against every
/// local snapshot variety (2026-11-09 probe), including tool calls, web search, reasoning content, and a
/// compaction-summarized conversation. This is what lets a companion process read history off shared storage
/// without reconstructing the full agent or holding model credentials.
/// </para>
/// <para>
/// A fresh dummy agent + history provider is built per call so no state bleeds between reads, and no
/// compaction reducer is wired — hydration wants the full stored transcript, not a compacted view.
/// </para>
/// </remarks>
public sealed class DurableHistoryReader
{
    private ISnapshotStore Snapshots { get; }

    /// <summary>
    /// Initializes the reader over a snapshot backend (the same <see cref="ISnapshotStore"/> the session
    /// store persists to).
    /// </summary>
    /// <param name="snapshots">The persistence backend holding the one session snapshot.</param>
    public DurableHistoryReader(ISnapshotStore snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        this.Snapshots = snapshots;
    }

    /// <summary>
    /// Reads the stored transcript and projects it for hydration. Returns an empty list when no context has
    /// been persisted yet (the client then starts blank).
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The projected conversation turns, oldest first.</returns>
    public async Task<IReadOnlyList<HistoryMessage>> ReadAsync(CancellationToken cancellationToken = default)
    {
        string? snapshot = await this.Snapshots.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return [];
        }

        // A throwaway minimal agent over a dummy chat client, built fresh per call. Only the history provider
        // is needed to read the transcript; the Disable* flags mirror the writer's shape so the stored
        // provider slices map cleanly.
        using var dummy = new DummyChatClient();
        var historyProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions());
        AIAgent agent = dummy.AsHarnessAgent(new HarnessAgentOptions
        {
            Name = "history-reader",
            ChatHistoryProvider = historyProvider,
            DisableFileMemory = true,
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
        });

        JsonElement serialized = JsonSerializer.Deserialize<JsonElement>(snapshot);
        AgentSession session = await agent
            .DeserializeSessionAsync(serialized, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        List<ChatMessage> stored = historyProvider.GetMessages(session);
        return Project(stored);
    }

    // Projects stored messages to the display shape. The show_images tool's result is stored in a tool-role
    // message that sits between the assistant turn that called it and the assistant's text answer; buffer any
    // images seen since the last emitted turn and hang them on the next assistant text message, so a reopened
    // client renders the gallery beneath the same answer it appeared under live.
    private static List<HistoryMessage> Project(List<ChatMessage> messages)
    {
        var history = new List<HistoryMessage>(messages.Count);
        var pendingImages = new List<ImageItem>();

        foreach (ChatMessage message in messages)
        {
            if (message.Role == ChatRole.Tool)
            {
                CollectImages(message, pendingImages);
                continue;
            }

            string role;
            if (message.Role == ChatRole.User)
            {
                role = "user";
            }
            else if (message.Role == ChatRole.Assistant)
            {
                role = "assistant";
            }
            else
            {
                continue;
            }

            string text = message.Text;
            if (string.IsNullOrWhiteSpace(text) is true)
            {
                continue;
            }

            ImageResult? images = null;
            if (message.Role == ChatRole.Assistant && pendingImages.Count > 0)
            {
                images = new ImageResult { Images = [.. pendingImages] };
                pendingImages.Clear();
            }

            history.Add(new HistoryMessage(role, text, images));
        }

        return history;
    }

    // Pulls any show_images results out of a stored tool-role message and appends their images to the sink.
    // An image result is a JSON object with an "images" array — distinct from the plan snapshot ("steps"), the
    // plan delta (a bare array), and plain-string tool results — so it is recognized the same way the live
    // streaming path recognizes it.
    private static void CollectImages(ChatMessage message, List<ImageItem> sink)
    {
        foreach (AIContent content in message.Contents)
        {
            if (content is not FunctionResultContent functionResult)
            {
                continue;
            }

            string? json = functionResult.Result switch
            {
                string s => s,
                JsonElement je => je.GetRawText(),
                _ => functionResult.Result?.ToString()
            };

            if (string.IsNullOrWhiteSpace(json) is true)
            {
                continue;
            }

            // Only an image result is object-shaped; skip arrays/strings without parsing.
            ReadOnlySpan<char> jsonSpan = json.AsSpan().TrimStart();
            if (jsonSpan.IsEmpty || jsonSpan[0] != '{')
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || doc.RootElement.TryGetProperty("images", out _) is false)
                {
                    continue;
                }

                ImageResult? result = JsonSerializer.Deserialize(
                    json,
                    DurableHistoryJsonContext.Default.ImageResult);
                if (result is { Images.Count: > 0 })
                {
                    sink.AddRange(result.Images);
                }
            }
            catch (JsonException)
            {
                // Not a well-formed image result — skip it; hydration still returns the assistant's text.
            }
        }
    }
}

/// <summary>
/// A no-op <see cref="IChatClient"/> used only to construct the throwaway agent that
/// <see cref="DurableHistoryReader"/> uses to deserialize a session. The model is never invoked during a
/// history read, so every response path throws.
/// </summary>
internal sealed class DummyChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The history reader never calls the model.");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The history reader never calls the model.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        // Nothing to dispose.
    }
}

/// <summary>
/// Source-generated serialization for the reader's image-result parsing, so no reflection-based serializer is
/// needed to recover <see cref="ImageResult"/> from a stored tool result.
/// </summary>
[JsonSerializable(typeof(ImageResult))]
internal sealed partial class DurableHistoryJsonContext : JsonSerializerContext;
