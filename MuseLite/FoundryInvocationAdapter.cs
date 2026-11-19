using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyAssistant.DurableContext;

namespace MuseLite;

/// <summary>
/// Request-adaptation layer for the hosting platform's own invokers. The AG-UI endpoint on
/// <c>/invocations</c> expects an AG-UI <c>RunAgentInput</c> JSON body, but the platform's native
/// invokers send different shapes to the same route: the portal playground sends the raw typed
/// message as a <c>text/plain</c> body (observed live, 2026-11-15: rejected 415 by endpoint routing
/// before any handler ran), and a scheduled routine sends a small JSON body (<c>{"input": …}</c>
/// per the platform docs) that deserializes into a message-less <c>RunAgentInput</c> and produced
/// the observed empty "how can I help" runs. This adapter rewrites both shapes into a genuine
/// <c>RunAgentInput</c> <em>before</em> the AG-UI handler binds the body, so platform-originated
/// runs carry their prompt like any other turn.
/// </summary>
/// <remarks>
/// The synthesized <c>threadId</c> is a reserved <see cref="TransientSessionKeys"/> id — the AG-UI
/// host passes the thread id through as the session-store key, and the durable session store
/// branches on these ids so platform-originated runs can never read or write the user's one durable
/// context: routine runs are fully ephemeral, playground runs get a per-playground in-memory
/// context. Real AG-UI bodies (the Blazor client) pass through byte-for-byte untouched.
/// </remarks>
internal static partial class FoundryInvocationAdapter
{
    /// <summary>
    /// What <see cref="TryAdaptAsync"/> decided about a request, driving how the caller must
    /// continue the pipeline.
    /// </summary>
    internal enum AdaptationOutcome
    {
        /// <summary>Not this adapter's concern — continue the pipeline unchanged.</summary>
        NotAdapted,

        /// <summary>A routine-face payload was rewritten; continue the pipeline (SSE response is fine).</summary>
        AdaptedForRoutine,

        /// <summary>
        /// A playground-face payload was rewritten; the caller must also adapt the RESPONSE to
        /// plain text via <see cref="RunWithPlainTextResponseAsync"/>, because the portal
        /// playground speaks the raw-text invocations wire format (raw prompt in, plain text out —
        /// the shape of the platform's own C# invocations sample) and renders nothing from an
        /// AG-UI SSE stream.
        /// </summary>
        AdaptedForPlayground,

        /// <summary>The request was rejected and the 400 response already written — stop.</summary>
        Rejected,
    }

    /// <summary>
    /// JSON bodies larger than this are assumed to be genuine AG-UI turns and are passed through
    /// without inspection. Under client-managed history the AG-UI client resends the whole
    /// transcript each turn, so real chat bodies grow to many kilobytes, while the platform's
    /// native payloads are a bare prompt; skipping the sniff for large bodies avoids buffering
    /// every chat turn just to look at it.
    /// </summary>
    private const long MaxSniffBytes = 64 * 1024;

    /// <summary>Cap on how much of a payload is reproduced in a log line.</summary>
    private const int MaxLoggedBodyChars = 2000;

    /// <summary>
    /// Inspects a request to the AG-UI route and, when it carries a platform-native payload,
    /// rewrites it in place into an AG-UI <c>RunAgentInput</c>. Unrecognized payload shapes get a
    /// 400 with the raw body logged, so a new platform shape surfaces loudly instead of running the
    /// agent with an empty prompt.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="logger">Sink for adaptation and rejection diagnostics.</param>
    /// <returns>The decision; see <see cref="AdaptationOutcome"/> for what each value obliges the caller to do.</returns>
    public static async Task<AdaptationOutcome> TryAdaptAsync(HttpContext context, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(logger);

        if (HttpMethods.IsPost(context.Request.Method) is false
            || string.Equals(context.Request.Path.Value, "/invocations", StringComparison.OrdinalIgnoreCase) is false)
        {
            return AdaptationOutcome.NotAdapted;
        }

        string contentType = context.Request.ContentType ?? string.Empty;
        bool isJson = contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase);
        bool isPlainText = contentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase);
        if (isJson is false && isPlainText is false)
        {
            // Neither face we know; let the endpoint answer as it always has (e.g. 415).
            return AdaptationOutcome.NotAdapted;
        }

        if (isJson is true && context.Request.ContentLength is > MaxSniffBytes)
        {
            // Too big to be a platform-native payload — a genuine AG-UI turn. Don't buffer it.
            return AdaptationOutcome.NotAdapted;
        }

        context.Request.EnableBuffering();
        string body;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        }

        string? prompt;
        string threadId;
        if (isJson is true)
        {
            if (TryGetJsonPrompt(body, out prompt) is false)
            {
                // Valid-JSON AG-UI bodies (and malformed JSON) fall through to the endpoint
                // untouched; only a recognized-but-promptless platform shape is rejected below.
                context.Request.Body.Seek(0, SeekOrigin.Begin);
                if (prompt is null)
                {
                    return AdaptationOutcome.NotAdapted;
                }
            }

            threadId = TransientSessionKeys.RoutineSessionId;
        }
        else
        {
            prompt = body;
            string? platformSessionId = context.Request.Query["agent_session_id"].FirstOrDefault();
            threadId = TransientSessionKeys.PlaygroundSessionIdPrefix
                + (string.IsNullOrWhiteSpace(platformSessionId) is true ? Guid.NewGuid().ToString("N") : platformSessionId);
        }

        // Truncation is hoisted into a local because the argument-evaluation analyzer cannot see
        // that it is cheap; it only runs on the rare adapter-hit paths anyway.
        string loggedBody = Truncate(body);
        if (string.IsNullOrWhiteSpace(prompt) is true)
        {
            LogRejected(logger, contentType, loggedBody);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(
                "Unrecognized invocation payload. Expected an AG-UI RunAgentInput JSON body, a JSON body with a string \"input\" (or \"message\") property, or a text/plain prompt.",
                context.RequestAborted).ConfigureAwait(false);
            return AdaptationOutcome.Rejected;
        }

        LogAdapted(logger, isJson is true ? "routine" : "playground", contentType, threadId, loggedBody);
        ReplaceBody(context, BuildRunAgentInput(threadId, prompt));
        return isJson is true ? AdaptationOutcome.AdaptedForRoutine : AdaptationOutcome.AdaptedForPlayground;
    }

    /// <summary>
    /// Runs the rest of the pipeline with the response buffered, then rewrites the endpoint's
    /// AG-UI SSE output as a plain-text body carrying just the assistant's text. The portal
    /// playground's invocations wire format is raw text in, plain text out (the platform's own C#
    /// invocations sample does exactly this), so it renders nothing from an AG-UI event stream.
    /// Buffering sacrifices streaming — acceptable for a portal test surface.
    /// </summary>
    /// <param name="context">The request context (already adapted for the playground face).</param>
    /// <param name="next">The rest of the pipeline.</param>
    public static async Task RunWithPlainTextResponseAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        Stream originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        string raw = Encoding.UTF8.GetString(buffer.ToArray());
        byte[] outBytes;
        if (context.Response.StatusCode == StatusCodes.Status200OK)
        {
            string text = ExtractAssistantText(raw);
            outBytes = Encoding.UTF8.GetBytes(text.Length > 0 ? text : raw);
            context.Response.ContentType = "text/plain; charset=utf-8";
        }
        else
        {
            // Non-success (e.g. a binder 400): pass the buffered response through verbatim.
            outBytes = buffer.ToArray();
        }

        context.Response.ContentLength = outBytes.Length;
        await context.Response.Body.WriteAsync(outBytes, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Concatenates the assistant text deltas out of a buffered AG-UI SSE stream
    /// (<c>data: {"type":"TEXT_MESSAGE_CONTENT","delta":…}</c> lines), appending a failure note
    /// when the run finished unsuccessfully. Returns an empty string when the stream carried no
    /// recognizable text (the caller then falls back to the raw stream for diagnosability).
    /// </summary>
    /// <param name="sseBody">The full buffered SSE response body.</param>
    /// <returns>The assistant's text, or an empty string.</returns>
    internal static string ExtractAssistantText(string sseBody)
    {
        var text = new StringBuilder();
        string? failure = null;
        foreach (string line in sseBody.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("data:", StringComparison.Ordinal) is false)
            {
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(trimmed["data:".Length..].Trim());
                JsonElement root = document.RootElement;
                if (root.ValueKind is not JsonValueKind.Object
                    || root.TryGetProperty("type", out JsonElement type) is false)
                {
                    continue;
                }

                if (string.Equals(type.GetString(), "TEXT_MESSAGE_CONTENT", StringComparison.Ordinal) is true
                    && root.TryGetProperty("delta", out JsonElement delta) is true
                    && delta.ValueKind is JsonValueKind.String)
                {
                    text.Append(delta.GetString());
                }
                else if (string.Equals(type.GetString(), "RUN_FINISHED", StringComparison.Ordinal) is true
                    && root.TryGetProperty("outcome", out JsonElement outcome) is true
                    && outcome.TryGetProperty("type", out JsonElement outcomeType) is true
                    && string.Equals(outcomeType.GetString(), "success", StringComparison.Ordinal) is false)
                {
                    failure = outcome.GetRawText();
                }
            }
            catch (JsonException)
            {
                // A non-JSON data line (unexpected) is skipped; the raw-stream fallback covers it.
            }
        }

        if (failure is not null)
        {
            text.Append(text.Length > 0 ? "\n\n" : string.Empty).Append("[run did not finish successfully: ").Append(failure).Append(']');
        }

        return text.ToString();
    }

    /// <summary>
    /// Extracts the prompt from a small JSON body. Returns <see langword="false"/> with a
    /// <see langword="null"/> prompt for bodies that are not this adapter's concern (a genuine
    /// AG-UI <c>RunAgentInput</c> — recognized by its <c>messages</c> property — or malformed
    /// JSON), and <see langword="false"/> with an empty prompt for a recognized platform shape
    /// that still carries no usable text.
    /// </summary>
    /// <param name="body">The raw JSON body.</param>
    /// <param name="prompt">The extracted prompt, when one was found.</param>
    /// <returns><see langword="true"/> when a usable prompt was extracted.</returns>
    private static bool TryGetJsonPrompt(string body, out string? prompt)
    {
        prompt = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;

            // A bare JSON string is its own prompt.
            if (root.ValueKind is JsonValueKind.String)
            {
                prompt = root.GetString();
                return string.IsNullOrWhiteSpace(prompt) is false;
            }

            if (root.ValueKind is not JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("messages", out _) is true)
            {
                // Genuine AG-UI RunAgentInput — not ours to touch.
                return false;
            }

            // "input" is the shape the Routines action schema documents; "message" is the shape the
            // platform's default invocations hosting accepts. Take either.
            if (root.TryGetProperty("input", out JsonElement input) is true
                || root.TryGetProperty("message", out input) is true)
            {
                if (input.ValueKind is JsonValueKind.String)
                {
                    prompt = input.GetString();
                    return string.IsNullOrWhiteSpace(prompt) is false;
                }

                // Recognized platform shape with an unexpected input type — reject (and log) so
                // the real shape gets observed rather than silently running an empty turn.
                prompt = string.Empty;
                return false;
            }

            // JSON object with neither messages nor input/message: the silent-empty-run shape. Reject.
            prompt = string.Empty;
            return false;
        }
        catch (JsonException)
        {
            // Not JSON after all; let the endpoint produce its own error.
            return false;
        }
    }

    /// <summary>
    /// Serializes a minimal AG-UI <c>RunAgentInput</c> carrying the prompt as the one user message.
    /// Property names must match the AG-UI wire schema exactly (camelCase), since the endpoint's
    /// body binder is what consumes this.
    /// </summary>
    /// <param name="threadId">The reserved transient thread id for this run.</param>
    /// <param name="prompt">The user prompt.</param>
    /// <returns>The UTF-8 encoded body.</returns>
    private static byte[] BuildRunAgentInput(string threadId, string prompt)
    {
        var run = new JsonObject
        {
            ["threadId"] = threadId,
            ["runId"] = Guid.NewGuid().ToString("N"),
            ["state"] = new JsonObject(),
            ["messages"] = new JsonArray(new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["role"] = "user",
                ["content"] = prompt,
            }),
            ["tools"] = new JsonArray(),
            ["context"] = new JsonArray(),
            ["forwardedProps"] = new JsonObject(),
        };

        return Encoding.UTF8.GetBytes(run.ToJsonString());
    }

    /// <summary>
    /// Swaps the request body for the synthesized one and fixes up the headers the body binder
    /// reads. The replacement stream is tied to the response lifetime for disposal.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="bodyBytes">The replacement body.</param>
    private static void ReplaceBody(HttpContext context, byte[] bodyBytes)
    {
        var replacement = new MemoryStream(bodyBytes);
        context.Response.RegisterForDispose(replacement);
        context.Request.Body = replacement;
        context.Request.ContentLength = bodyBytes.Length;
        context.Request.ContentType = "application/json";
    }

    /// <summary>Caps a payload string for logging.</summary>
    /// <param name="body">The raw payload.</param>
    /// <returns>The payload, truncated with a marker when over the cap.</returns>
    private static string Truncate(string body)
    {
        return body.Length <= FoundryInvocationAdapter.MaxLoggedBodyChars
            ? body
            : body[..FoundryInvocationAdapter.MaxLoggedBodyChars] + " …(truncated)";
    }

    /// <summary>Logs a successful adaptation, including the raw body so new platform payload shapes get observed.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="face">Which invoker face was recognized (routine or playground).</param>
    /// <param name="contentType">The original Content-Type.</param>
    /// <param name="threadId">The reserved thread id assigned to the run.</param>
    /// <param name="body">The original (truncated) payload.</param>
    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Adapted platform-native invocation ({Face}, Content-Type '{ContentType}') to AG-UI thread '{ThreadId}'. Original body: {Body}")]
    private static partial void LogAdapted(ILogger logger, string face, string contentType, string threadId, string body);

    /// <summary>Logs a rejected payload with its raw body, so an unknown shape can be diagnosed from the logs alone.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="contentType">The original Content-Type.</param>
    /// <param name="body">The original (truncated) payload.</param>
    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Rejected unrecognized /invocations payload (Content-Type '{ContentType}'). Body: {Body}")]
    private static partial void LogRejected(ILogger logger, string contentType, string body);
}
