using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MyAssistantServer.AgenticUI;

/// <summary>
/// Debug-level diagnostic that surfaces the real cause of a streaming failure. The AG-UI hosting layer
/// catches an exception thrown while streaming the agent response, converts it to a generic client-side
/// error (RUN_ERROR / "An error occurred while streaming the agent response"), and does not log the
/// underlying cause on the server. Wrapping the base agent from the inside, this logs the real exception
/// (and any <see cref="ErrorContent"/> that arrives as a normal update) before the host swallows it, then
/// rethrows so behavior is unchanged. Off by default; enable by raising this category to <c>Debug</c> in
/// the Logging configuration (e.g. <c>"MyAssistantServer.AgenticUI": "Debug"</c>).
/// </summary>
internal sealed class StreamErrorLoggingAgent : DelegatingAIAgent
{
    private ILogger Logger { get; }

    public StreamErrorLoggingAgent(AIAgent innerAgent, ILogger<StreamErrorLoggingAgent> logger)
        : base(innerAgent)
    {
        this.Logger = logger;
    }

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return this.RunStreamingAsync(messages, session, options, cancellationToken).ToAgentResponseAsync(cancellationToken);
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IAsyncEnumerator<AgentResponseUpdate> enumerator =
            this.InnerAgent.RunStreamingAsync(messages, session, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                AgentResponseUpdate update;
                try
                {
                    if (await enumerator.MoveNextAsync().ConfigureAwait(false) is false)
                    {
                        break;
                    }

                    update = enumerator.Current;
                }
                // S2139: deliberately log-then-rethrow — the point is to surface the real cause on the server
                // before the AG-UI host turns it into a generic error, while leaving the exception otherwise
                // unchanged. CA1848: LoggerMessage delegates are over-engineering for an opt-in diagnostic.
#pragma warning disable S2139
                catch (Exception ex)
                {
#pragma warning disable CA1848
                    this.Logger.LogDebug(ex, "Agent streaming threw (surfaced before the AG-UI host swallows it).");
#pragma warning restore CA1848
                    throw;
                }
#pragma warning restore S2139

                if (this.Logger.IsEnabled(LogLevel.Debug) is true)
                {
                    foreach (ErrorContent error in update.Contents.OfType<ErrorContent>())
                    {
#pragma warning disable CA1848
                        this.Logger.LogDebug(
                            "Agent returned ErrorContent: code={Code} message={Message}",
                            error.ErrorCode,
                            error.Message);
#pragma warning restore CA1848
                    }
                }

                yield return update;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }
}
