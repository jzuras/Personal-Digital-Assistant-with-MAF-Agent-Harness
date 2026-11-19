using System.Net.Http.Headers;

namespace MuseLite;

/// <summary>
/// Executes routine-face agent runs in the background, decoupled from the dispatch request.
/// The platform's Routine dispatcher enforces a 30-second per-attempt timeout and only requires a
/// 2xx acceptance — dispatch is an acceptance handshake, with the agent's real work expected to be
/// asynchronous. A daily-task turn takes minutes, so it can never finish inline; this launcher lets
/// the dispatch middleware acknowledge immediately and run the turn to completion afterward.
/// </summary>
/// <remarks>
/// The run is executed by POSTing the synthesized AG-UI <c>RunAgentInput</c> back to this server's
/// own <c>/invocations</c> endpoint over loopback, which reuses the entire proven serving pipeline
/// (harness loop, tools, the ephemeral routine session branch) instead of re-implementing agent
/// execution against preview APIs. A single in-flight gate deduplicates dispatches: the platform
/// retries an unacknowledged dispatch with the identical payload, and overlapping daily-task runs
/// must not stack.
/// </remarks>
internal sealed partial class RoutineRunLauncher
{
    /// <summary>
    /// Longest text of a finished run reproduced in the completion log line. Sized to carry a full
    /// daily-digest output (realistically a few KB) — the whole point of the line is unattended-run
    /// forensics — while still bounding a pathological runaway output (the model's output cap is
    /// 120k tokens) below the ~32 KB where App Insights truncates trace messages.
    /// </summary>
    private const int MaxLoggedTextChars = 16_000;

    private IHttpClientFactory ClientFactory { get; }

    private IHostApplicationLifetime Lifetime { get; }

    private ILogger Logger { get; }

    private Func<string> LoopbackBaseAddress { get; }

    /// <summary>
    /// Single-run gate: a dispatch that arrives while a routine run is in flight is acknowledged
    /// without starting a second run.
    /// </summary>
    private SemaphoreSlim InFlightGate { get; } = new(1, 1);

    /// <summary>
    /// Initializes the launcher.
    /// </summary>
    /// <param name="clientFactory">Factory for the loopback HTTP client.</param>
    /// <param name="lifetime">Host lifetime; a background run is cancelled on shutdown.</param>
    /// <param name="logger">Sink for run start/completion/failure diagnostics.</param>
    /// <param name="loopbackBaseAddress">
    /// Resolves this server's own base address at launch time (listen addresses are only known
    /// after startup, so this is a deferred lookup rather than a constructor value).
    /// </param>
    public RoutineRunLauncher(
        IHttpClientFactory clientFactory,
        IHostApplicationLifetime lifetime,
        ILogger logger,
        Func<string> loopbackBaseAddress)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(loopbackBaseAddress);

        this.ClientFactory = clientFactory;
        this.Lifetime = lifetime;
        this.Logger = logger;
        this.LoopbackBaseAddress = loopbackBaseAddress;
    }

    /// <summary>
    /// Starts a background run for the given AG-UI <c>RunAgentInput</c> body, unless one is
    /// already in flight.
    /// </summary>
    /// <param name="runAgentInput">The UTF-8 encoded <c>RunAgentInput</c> JSON to execute.</param>
    /// <returns><see langword="true"/> if a run was started; <see langword="false"/> if a run is already in progress.</returns>
    public bool TryLaunch(byte[] runAgentInput)
    {
        ArgumentNullException.ThrowIfNull(runAgentInput);

        if (this.InFlightGate.Wait(0, CancellationToken.None) is false)
        {
            return false;
        }

        _ = Task.Run(() => this.RunAsync(runAgentInput), CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Runs the turn over loopback, drains the SSE response, and logs the outcome. Always releases
    /// the in-flight gate.
    /// </summary>
    /// <param name="runAgentInput">The UTF-8 encoded <c>RunAgentInput</c> JSON to execute.</param>
    private async Task RunAsync(byte[] runAgentInput)
    {
        try
        {
            string baseAddress = this.LoopbackBaseAddress();
            LogRunStarted(this.Logger, baseAddress);

            using HttpClient client = this.ClientFactory.CreateClient();
            // A daily-task turn can run for many minutes; the host-shutdown token is the only bound.
            client.Timeout = Timeout.InfiniteTimeSpan;

            using var content = new ByteArrayContent(runAgentInput);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            using var request = new HttpRequestMessage(HttpMethod.Post, baseAddress + "/invocations") { Content = content };

            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, this.Lifetime.ApplicationStopping)
                .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(this.Lifetime.ApplicationStopping).ConfigureAwait(false);

            string text = FoundryInvocationAdapter.ExtractAssistantText(body);
            // Keep the TAIL when truncating: a run's text starts with step-by-step narration and
            // ends with the final summary, and the summary is what a log reader needs.
            string snippet = text.Length > RoutineRunLauncher.MaxLoggedTextChars
                ? "(truncated)… " + text[^RoutineRunLauncher.MaxLoggedTextChars..]
                : text;
            LogRunCompleted(this.Logger, (int)response.StatusCode, snippet.Length > 0 ? snippet : body);
        }
        catch (OperationCanceledException)
        {
            LogRunCancelled(this.Logger);
        }
#pragma warning disable CA1031 // A background run has no caller to observe an exception; log-and-release is the only correct handling.
        catch (Exception exception)
        {
            LogRunFailed(this.Logger, exception);
        }
#pragma warning restore CA1031
        finally
        {
            this.InFlightGate.Release();
        }
    }

    /// <summary>Logs the start of a background routine run.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="baseAddress">The loopback base address the run targets.</param>
    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Routine run started in background (loopback {BaseAddress}/invocations).")]
    private static partial void LogRunStarted(ILogger logger, string baseAddress);

    /// <summary>Logs completion of a background routine run with the assistant's (truncated) text.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="statusCode">The loopback response status code.</param>
    /// <param name="text">The assistant text (or raw body when no text was recognized), truncated.</param>
    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Routine run finished (loopback status {StatusCode}). Assistant output: {Text}")]
    private static partial void LogRunCompleted(ILogger logger, int statusCode, string text);

    /// <summary>Logs cancellation of a background routine run during host shutdown.</summary>
    /// <param name="logger">The sink.</param>
    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "Routine run cancelled (host shutting down).")]
    private static partial void LogRunCancelled(ILogger logger);

    /// <summary>Logs a failed background routine run.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="exception">The failure.</param>
    [LoggerMessage(EventId = 4, Level = LogLevel.Error,
        Message = "Routine run failed.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}
