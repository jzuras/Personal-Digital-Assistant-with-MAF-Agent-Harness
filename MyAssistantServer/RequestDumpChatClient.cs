using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;

namespace MyAssistantServer.AgenticUI;

/// <summary>
/// Debug-level diagnostic that logs the full outbound message history (roles + content kinds) immediately
/// before it is sent to the model. Off by default; enable by raising this category to <c>Debug</c> in the
/// Logging configuration (e.g. <c>"MyAssistantServer.AgenticUI": "Debug"</c>). Sits at the bottom of the
/// harness chat-client stack, so it shows the final assembled request — useful for diagnosing Responses-API
/// payload problems under client-managed history, where the whole transcript is replayed each turn.
/// </summary>
internal sealed class RequestDumpChatClient : DelegatingChatClient
{
    private ILogger Logger { get; }

    public RequestDumpChatClient(IChatClient innerClient, ILogger<RequestDumpChatClient> logger)
        : base(innerClient)
    {
        this.Logger = logger;
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        this.Dump(messages);
        return this.InnerClient.GetResponseAsync(messages, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        this.Dump(messages);
        return this.InnerClient.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    private void Dump(IEnumerable<ChatMessage> messages)
    {
        if (this.Logger.IsEnabled(LogLevel.Debug) is false)
        {
            return;
        }

        IReadOnlyList<ChatMessage> list = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        var sb = new StringBuilder();
        for (int i = 0; i < list.Count; i++)
        {
            ChatMessage message = list[i];
            IEnumerable<string> parts = message.Contents.Select(Describe);
            sb.AppendLine(CultureInfo.InvariantCulture, $"  [{i}] {message.Role}: {string.Join(", ", parts)}");
        }

        // CA1848 (LoggerMessage delegates) and CA1873 (expensive-arg evaluation) are intentional here: this
        // is opt-in, off-by-default diagnostic logging already guarded by the IsEnabled check above, so the
        // performance machinery those rules want would be over-engineering.
#pragma warning disable CA1848, CA1873
        this.Logger.LogDebug("Outbound history ({Count} messages):\n{History}", list.Count, sb.ToString());
#pragma warning restore CA1848, CA1873
    }

    private static string Describe(AIContent content)
    {
        return content switch
        {
            TextContent t => $"Text(\"{Trim(t.Text)}\")",
            TextReasoningContent r => $"Reasoning(len={r.Text?.Length ?? 0})",
            FunctionCallContent fc => $"FuncCall({fc.Name} id={fc.CallId})",
            FunctionResultContent fr => $"FuncResult(id={fr.CallId})",
            UsageContent => "Usage",
            _ => content.GetType().Name,
        };
    }

    private static string Trim(string? s)
    {
        string text = s ?? string.Empty;
        return text.Length <= 40 ? text : text[..40] + "...";
    }
}
