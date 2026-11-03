using Microsoft.Extensions.AI;

namespace MyAssistantServer.AgenticUI;

/// <summary>
/// Removes hosted-tool call/result items from the message history before it is sent to the model.
/// </summary>
/// <remarks>
/// The server runs with stored output disabled (client-managed history), so the full transcript is replayed
/// as input on every request. Hosted tools (web search, code interpreter, image generation, MCP server)
/// execute entirely server-side within a single round-trip, and their call/result items are OUTPUT
/// artifacts, not valid input items; replaying them makes the Responses API reject the request with HTTP 400
/// invalid_payload ("does not match the expected schema"). The model's own reasoning and text from that
/// round-trip already capture what the tool produced, so dropping the raw items on replay loses nothing.
/// This mirrors the framework's own StatelessWebSearchChatClient sample, generalized to every hosted-tool
/// content type (<see href="https://github.com/microsoft/agent-framework/pull/8708"/>,
/// <see href="https://github.com/Azure/azure-sdk-for-net/issues/58444"/>). Custom function calls/results
/// (<see cref="FunctionCallContent"/> / <see cref="FunctionResultContent"/>) are preserved — they are
/// replayable and the model needs them.
/// </remarks>
internal sealed class HostedToolHistoryStrippingChatClient : DelegatingChatClient
{
    public HostedToolHistoryStrippingChatClient(IChatClient innerClient)
        : base(innerClient)
    {
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return this.InnerClient.GetResponseAsync(Strip(messages), options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return this.InnerClient.GetStreamingResponseAsync(Strip(messages), options, cancellationToken);
    }

    // Hosted-tool content types follow the convention <Tool>ToolCallContent / <Tool>ToolResultContent
    // (WebSearch, CodeInterpreter, ImageGeneration, McpServer). Custom-function content is
    // FunctionCallContent / FunctionResultContent, which do NOT end in those suffixes and so are preserved.
    // Matching by type-name suffix keeps this general across hosted tools (and any future one that follows
    // the convention) without a compile-time dependency on each preview content type.
    private static bool IsHostedToolContent(AIContent content)
    {
        string name = content.GetType().Name;
        return name.EndsWith("ToolCallContent", StringComparison.Ordinal)
            || name.EndsWith("ToolResultContent", StringComparison.Ordinal);
    }

    private static IEnumerable<ChatMessage> Strip(IEnumerable<ChatMessage> messages)
    {
        foreach (ChatMessage message in messages)
        {
            if (message.Contents.Any(IsHostedToolContent) is false)
            {
                yield return message;
                continue;
            }

            List<AIContent> kept = [.. message.Contents.Where(c => IsHostedToolContent(c) is false)];
            if (kept.Count == 0)
            {
                // The whole message was hosted-tool artifacts; drop it — nothing replayable remains.
                continue;
            }

            yield return new ChatMessage(message.Role, kept)
            {
                AuthorName = message.AuthorName,
                MessageId = message.MessageId,
                AdditionalProperties = message.AdditionalProperties,
                RawRepresentation = message.RawRepresentation,
            };
        }
    }
}
