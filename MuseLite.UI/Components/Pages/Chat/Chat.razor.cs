using System.Text;
using System.Text.Json;
using AGUI.Abstractions;
using MuseLite.UI.Models;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.AI;

namespace MuseLite.UI.Components.Pages.Chat;

/// <summary>
/// Code-behind for the main chat page. Handles streaming responses from the AG-UI
/// agent and extracts predictive state snapshots and JSON patch deltas.
/// </summary>
public partial class Chat : IAsyncDisposable
{
    [Inject]
    private ILogger<Chat> Logger { get; set; } = default!;

    [Inject]
    private AIAgent Agent { get; set; } = default!;

    [Inject]
    private IHttpClientFactory HttpClientFactory { get; set; } = default!;

    [Inject]
    private IConfiguration Configuration { get; set; } = default!;

    private static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    private AgentSession? ActiveSession { get; set; }
    private List<ChatMessage> Messages { get; } = [];
    private ChatInput? ChatInput { get; set; }
    private int StatefulMessageCount { get; set; }
    private string SessionId { get; set; } = Guid.Empty.ToString();

    /// <summary>
    /// Stable AG-UI thread id for this client instance, pinned on every turn (including approval-resume)
    /// via <see cref="ChatOptions.RawRepresentationFactory"/>. The server's <c>MyAgentSessionStore</c> routes
    /// any thread id to its one durable context, but a <em>stable</em> id is what lets an approval resume land
    /// on the same server session that holds the pending-approval binding (PR #8641 binding is ON). Created
    /// once per component instance; a page reload mints a new one (see the resume caveat in the design notes).
    /// </summary>
    private string ThreadId { get; } = Guid.NewGuid().ToString();
    private ChatMessage? CurrentResponseMessage { get; set; }
    private CancellationTokenSource? CurrentResponseCancellation { get; set; }
    private AgentState? AgentState { get; set; }

    /// <summary>
    /// Holds a pending approval request surfaced by a gated server tool. When non-null, the UI
    /// shows the tool + its arguments with Approve/Deny; the decision is sent back on the same
    /// session. Showing the arguments matters because approval can be keyed on the exact tool+args
    /// combination (e.g. record_note with a specific message).
    /// </summary>
    private ToolApprovalRequestContent? PendingApproval { get; set; }

    /// <summary>
    /// Gates the two-step "new conversation" confirmation. The header button sets this; the inline card
    /// then states what a reset keeps (the durable memory files + standing approvals) vs. discards (the
    /// running transcript) before the user commits. A deliberate confirm because the reset archives the
    /// conversation.
    /// </summary>
    private bool ResetConfirmVisible { get; set; }

    /// <summary>
    /// Timestamp of the most recent streaming activity for the in-progress response — set when a response
    /// is created and refreshed on every streamed update — or <see langword="null"/> before the first turn.
    /// Anchors the reset-block timeout on <em>inactivity</em>, not total elapsed time: a healthy long turn
    /// (e.g. one that streams a multi-step plan card by card, with pauses between steps) keeps refreshing
    /// this and so keeps reset disabled, while a turn that errors out or hangs stops refreshing it and lets
    /// the timeout re-enable reset. Only read while <see cref="CurrentResponseMessage"/> is non-null.
    /// </summary>
    private DateTimeOffset? LastResponseActivityUtc { get; set; }

    /// <summary>
    /// True while a "new conversation" reset request is in flight to the server. Blocks sending a new
    /// prompt during the archive, so a fresh turn's snapshot write cannot race the reset. Pairs with the
    /// storage-level ETag <c>If-Match</c> backstop on the archive (see <c>BlobSnapshotStore</c>).
    /// </summary>
    private bool ResetInFlight { get; set; }

    /// <summary>
    /// How long the in-progress response may go WITHOUT any streamed update before "New conversation" is
    /// re-enabled regardless (measured from <see cref="LastResponseActivityUtc"/>). The pending flag normally
    /// clears when the turn completes; this is the escape hatch for a turn that errors out or hangs without
    /// clearing it. Because it measures inactivity rather than total elapsed time, a legitimately long but
    /// actively-streaming turn (many plan steps, inter-step delays) never trips it.
    /// </summary>
    private static TimeSpan ResetBlockTimeout { get; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Whether "New conversation" is currently disabled: the agent is offline/connecting, the custom
    /// operations (which perform the archive) are unreachable, a reset is already in flight, or a response is
    /// streaming and has produced an update within <see cref="ResetBlockTimeout"/> (after that long with no
    /// update, a presumed-stuck turn no longer blocks). Disabling reset during a live turn keeps the reset
    /// archive from racing the server's per-turn snapshot write; the ETag backstop covers the residual window.
    /// </summary>
    private bool ResetBlocked =>
        this.ConnectionPending
        || this.ResetInFlight
        || this.OperationsAvailable is false
        || (this.CurrentResponseMessage is not null
            && this.LastResponseActivityUtc is { } lastActivityUtc
            && DateTimeOffset.UtcNow - lastActivityUtc < ResetBlockTimeout);

    /// <summary>Hover explanation for a disabled "New conversation" button, or <see langword="null"/> when enabled.</summary>
    private string? NewConversationDisabledReason => this switch
    {
        { OperationsAvailable: false } => "The conversation store is unreachable right now, so a reset isn't available.",
        { ConnectionPending: true } => "Connecting to the assistant…",
        { ResetBlocked: true } => "Please wait for the current response to finish.",
        _ => null,
    };

    /// <summary>Hover explanation for a disabled "Always approve" button, or <see langword="null"/> when enabled.</summary>
    private string? AlwaysApproveDisabledReason =>
        (this.OperationsAvailable is false)
            ? "Saving a standing approval needs the conversation store, which is unreachable right now. You can still approve once."
            : null;

    /// <summary>
    /// Current reachability of the agent server. Drives the connection banner and gates input — the client
    /// can do nothing useful until the server is <see cref="ConnectionStatus.Online"/>. Starts
    /// <see cref="ConnectionStatus.Connecting"/> on load; mostly a dev-env concern, since the client is
    /// often started before the server.
    /// </summary>
    private ConnectionStatus Connection { get; set; } = ConnectionStatus.Connecting;

    /// <summary>
    /// Current reachability of the server's custom durable operations (history / reset / remember), which are
    /// storage-backed and so can diverge from chat reachability (the container can be up while a durable read
    /// fails). Probed via the history op, independent of the chat warm-up: these being down degrades only
    /// those features and NEVER gates chat. Starts <see cref="ConnectionStatus.Connecting"/>; driven by the
    /// same background loop as the agent.
    /// </summary>
    private ConnectionStatus OperationsConnection { get; set; } = ConnectionStatus.Connecting;

    /// <summary>True when the custom operations are reachable, so history/reset/remember are available.</summary>
    private bool OperationsAvailable => this.OperationsConnection == ConnectionStatus.Online;

    /// <summary>True when the custom operations are unreachable — binds the disabled state of their dependent controls.</summary>
    private bool OperationsUnavailable => this.OperationsAvailable is false;

    /// <summary>Cancels the background auto-reconnect loop (on success, a manual retry restart, or dispose).</summary>
    private CancellationTokenSource? ConnectionLoopCancellation { get; set; }

    /// <summary>The running auto-reconnect loop, awaited on dispose so it unwinds cleanly.</summary>
    private Task? ConnectionLoopTask { get; set; }

    /// <summary>Backoff bounds for the auto-reconnect loop.</summary>
    private const int InitialRetryDelayMs = 1_000;
    private const int MaxRetryDelayMs = 8_000;

    /// <summary>
    /// True once the stored transcript has been hydrated on the first successful connect. Guards against
    /// RE-hydrating on a mid-session reconnect: the UI already holds the live conversation, so re-painting
    /// the server's copy on top would duplicate it (the "whole conversation reappears as if refreshed" bug).
    /// On a reconnect we keep the existing messages and simply go back online.
    /// </summary>
    private bool InitialHydrationDone { get; set; }

    /// <summary>
    /// The assistant message carrying the "went offline" notice for a turn whose connection dropped before
    /// a reply came back. Held so that when the server returns we can update its text from "Reconnecting…"
    /// to a resend hint, rather than leaving the stale reconnecting message on screen. Whether the server
    /// actually received the turn is unknown (the drop only tells us no reply arrived), so the turn is never
    /// auto-resent — a resend could duplicate it — and the notice hedges accordingly.
    /// </summary>
    private ChatMessage? OfflineNoticeMessage { get; set; }

    /// <summary>
    /// AdditionalProperties key set on the follow-up assistant message when a tool approval is
    /// denied, so <c>ChatMessageItem</c> can render a subtle inline "denied" marker. Client-only —
    /// it is display state, never sent to the agent as content.
    /// </summary>
    internal const string ToolDeniedPropertyKey = "tool_denied";

    /// <summary>
    /// AdditionalProperties key set on the follow-up assistant message when the user chose "Always
    /// approve" and the standing grant was saved, so <c>ChatMessageItem</c> can render a subtle inline
    /// "will auto-approve from now on" marker. Client-only display state, never sent to the agent.
    /// </summary>
    internal const string ToolRememberedPropertyKey = "tool_remembered";

    /// <summary>
    /// AdditionalProperties key set on the follow-up assistant message when the user chose "Always
    /// approve" but the grant could NOT be saved (the tool is human-only, or the request failed) — the
    /// call is still approved for this one time, and <c>ChatMessageItem</c> renders a subtle "approved
    /// once; couldn't be remembered" marker. Client-only display state, never sent to the agent.
    /// </summary>
    internal const string ToolRememberFailedPropertyKey = "tool_remember_failed";

    /// <summary>
    /// AdditionalProperties key under which the <c>show_images</c> tool's <see cref="ImageResult"/> is
    /// stashed on the assistant message that produced it, so <c>ChatMessageItem</c> can render the image
    /// gallery beneath that answer (and it stays with the answer in scrollback). Client-only display state,
    /// never sent to the agent.
    /// </summary>
    internal const string ImagesPropertyKey = "images";

    // Private Use Area characters OpenAI uses to delimit inline web-search citation markers: U+E200 opens a
    // marker, U+E201 closes it, U+E202 separates "cite" from the refs (the run that surfaces as the garbage
    // "citeturn1search7" glyphs). Referenced by code point (0xE200...) so the source carries no invisible char.
    private const char CitationStartMarker = (char)0xE200;
    private const char CitationEndMarker = (char)0xE201;
    private const char CitationResidueMax = (char)0xE206;

    /// <summary>
    /// Removes OpenAI web-search inline citation markers from assistant text for display. When an answer is
    /// grounded in web search, the model embeds citation tokens delimited by Private Use Area characters
    /// (U+E200 start, U+E201 end, with a U+E202 separator) that surface as a garbage "citeturn1search7" run.
    /// The citation targets (url/title) are not carried in our stored history, so there is nothing to turn
    /// them into real links; the markers are pure display noise. This strips each span (plus any stray
    /// citation-range PUA residue from a partial span mid-stream) and leaves the durable snapshot untouched
    /// -- it is a render-time, display-only cleanup applied to both live and hydrated text.
    /// </summary>
    internal static string StripCitationMarkers(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Contains(CitationStartMarker, StringComparison.Ordinal) is false)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        bool insideMarker = false;
        foreach (char c in text)
        {
            if (insideMarker is true)
            {
                // Drop everything through the closing delimiter (an unterminated marker runs to the end).
                if (c == CitationEndMarker)
                {
                    insideMarker = false;
                }

                continue;
            }

            if (c == CitationStartMarker)
            {
                insideMarker = true;
                continue;
            }

            // Drop any stray citation-range PUA residue sitting outside a well-formed marker.
            if (c is > CitationStartMarker and <= CitationResidueMax)
            {
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Gates the deliberate second step after the user clicks "Always approve" on a pending approval:
    /// the approval panel swaps its action buttons for a confirm card stating that the grant is permanent
    /// and how to revoke it, before anything is written. A permanent, cross-conversation grant is a
    /// deliberate act, so it is never one click.
    /// </summary>
    private bool ApprovalRememberConfirmVisible { get; set; }

    private string? PendingApprovalToolName => (this.PendingApproval?.ToolCall as FunctionCallContent)?.Name;

    /// <summary>
    /// The pending tool call's arguments as display-ready key/value pairs, for the approval prompt.
    /// Empty when there is no pending request or the tool takes no arguments.
    /// </summary>
    private IReadOnlyList<KeyValuePair<string, string>> PendingApprovalArgs =>
        this.PendingApproval?.ToolCall is FunctionCallContent { Arguments: { Count: > 0 } args }
            ? [.. args.Select(kv => new KeyValuePair<string, string>(kv.Key, FormatArgValue(kv.Value)))]
            : [];

    /// <summary>
    /// Renders a tool-argument value for display/logging. Over the wire, argument values arrive as
    /// <see cref="JsonElement"/>; strings are unwrapped, everything else shown as raw JSON.
    /// </summary>
    private static string FormatArgValue(object? value) => value switch
    {
        null => "null",
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString() ?? string.Empty,
        JsonElement je => je.GetRawText(),
        _ => value.ToString() ?? string.Empty,
    };

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        // Everything here is for the live client — creating the session, reaching the agent server, and (if
        // the server is not up yet) running a reconnect loop. During static prerender the component is a
        // throwaway instance with no circuit: it cannot keep a loop running or react to the user, so skip it
        // all and let the interactive render do the work. This also avoids duplicate startup probes and logs
        // (prerender + interactive each run OnInitializedAsync).
        if (this.RendererInfo.IsInteractive is false)
        {
            return;
        }

        this.ActiveSession = await this.Agent.CreateSessionAsync(CancellationToken.None);
        this.SessionId = Guid.NewGuid().ToString();
        this.Logger.LogInformation("Started session {SessionId}", this.SessionId);

        // Two independent reachability checks, both against the agent server, run by the background loop
        // (below) rather than awaited here, so a slow agent cold-start never blocks the first paint:
        //  • CHAT — reachability is a no-op "warm-up" invocation, because a hosted Foundry agent exposes no
        //    client-reachable readiness endpoint. Chat input is gated on this: no agent, no chat.
        //  • the custom OPERATIONS (history / reset / remember) — reachability is the history op, which
        //    doubles as hydration and, being storage-backed, can fail while chat is up. This is SOFT: it
        //    never gates chat, only the features that depend on it.
        // InitialConnectDone is set now (interactive only) so the connecting/offline banner and the disabled
        // input reflect the real agent state from the first interactive render while the warm-up is in flight.
        this.InitialConnectDone = true;
        this.StartConnectionLoop();
    }

    /// <summary>
    /// Makes one attempt to reach the agent by POSTing a no-op <c>x-client-warmup</c> invocation. A hosted
    /// Foundry agent exposes no client-reachable readiness endpoint, so this synthesizes one on the only
    /// path the gateway proxies (<c>invocations</c>): the server answers the header with <c>204</c> before
    /// the agent runs, so no model is called and no durable state is written — and the inbound request alone
    /// wakes a scaled-to-zero micro-VM, so a reachable agent is also a warm one for the first real turn. Any
    /// HTTP response means reachable; a connection-level failure (<see cref="HttpRequestException"/> with no
    /// status, or a timeout) means not. Pure I/O with no component-state mutation, safe to call from the
    /// background loop; callers apply the result on the render thread. Identical local and hosted.
    /// </summary>
    private async Task<bool> WarmUpAgentAsync(CancellationToken cancellationToken)
    {
        try
        {
            string endpoint = this.Configuration["AgentEndpoint"] ?? "invocations";
            string query = this.Configuration["AgentEndpointQuery"] ?? string.Empty;
            HttpClient httpClient = this.HttpClientFactory.CreateClient("agent");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}{query}")
            {
                // A minimal body is enough — the probe confirmed the gateway forwards it and the server's
                // warm-up middleware short-circuits before the body is even parsed.
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("x-client-warmup", "true");

            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            this.Logger.LogDebug("Agent warm-up returned {Status}.", (int)response.StatusCode);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested is true)
        {
            // Our own cancellation (disposing, or a manual retry restarting the loop); not a reachability
            // signal. The caller discards this.
            return false;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            // No HTTP status => the request never reached the agent (connection refused / DNS / socket): the
            // "agent not running yet" signal. Logged once on the state transition, not per retry attempt.
            return false;
        }
        catch (TaskCanceledException)
        {
            // HttpClient request timeout (distinct from our own cancellation above) => unreachable.
            return false;
        }
    }

    /// <summary>
    /// Builds a POST to the agent's <c>/invocations</c> route carrying an <c>x-client-op</c> header — the
    /// gateway-legal way the client reaches the server's custom durable operations (history / reset /
    /// approvals). The protocol-only hosted gateway refuses sibling routes like <c>/history</c>, but forwards
    /// the <c>x-client-*</c> namespace, and the server dispatches on this header before any chat parsing. Uses
    /// the same endpoint + query + bearer auth as chat (the <c>"agent"</c> client), so one config value and
    /// one auth path cover chat and the custom ops in every environment.
    /// </summary>
    /// <param name="op">The operation name (<c>history</c> / <c>reset</c> / <c>approvals</c>).</param>
    /// <param name="content">The request body, or <see langword="null"/> for a body-less op.</param>
    /// <returns>The request message; the caller owns disposal and sends it on the <c>"agent"</c> client.</returns>
    private HttpRequestMessage CreateAgentOpRequest(string op, HttpContent? content)
    {
        string endpoint = this.Configuration["AgentEndpoint"] ?? "invocations";
        string query = this.Configuration["AgentEndpointQuery"] ?? string.Empty;
        var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}{query}") { Content = content };
        request.Headers.TryAddWithoutValidation("x-client-op", op);
        return request;
    }

    /// <summary>
    /// Makes one attempt to reach the server's history operation (POST <c>/invocations</c> with
    /// <c>x-client-op: history</c>). Reaching it (any HTTP response, including an empty history) means the
    /// server's custom ops are up; a connection-level failure means they are not. The call doubles as
    /// hydration: a clean reach carries the stored transcript back. Pure I/O with no component-state
    /// mutation, safe to call from the background loop; callers apply the result via
    /// <see cref="ApplyOperationsProbe"/> on the render thread. This state is SOFT — it never gates chat, only
    /// the features that depend on it (history / reset / remember).
    /// </summary>
    private async Task<OperationsProbe> ProbeOperationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            HttpClient httpClient = this.HttpClientFactory.CreateClient("agent");
            using HttpRequestMessage request = this.CreateAgentOpRequest(
                "history", new StringContent("{}", Encoding.UTF8, "application/json"));

            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var history = await response.Content.ReadFromJsonAsync<List<HistoryMessage>>(cancellationToken);
            return new OperationsProbe(Reached: true, History: history, Errored: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested is true)
        {
            // Our own cancellation; the caller discards this. Not a reachability signal.
            return new OperationsProbe(Reached: false, History: null, Errored: false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            // No HTTP status => the request never reached the server (connection refused / DNS / socket).
            // Logged once on the state transition, not per retry attempt.
            return new OperationsProbe(Reached: false, History: null, Errored: false);
        }
        catch (TaskCanceledException)
        {
            // HttpClient request timeout => unreachable.
            return new OperationsProbe(Reached: false, History: null, Errored: false);
        }
        catch (Exception ex)
        {
            // Reached the server but the history call itself failed (e.g. a non-success status from
            // EnsureSuccessStatusCode). Don't treat as offline over a data error — reachable, empty chat.
            this.Logger.LogWarning(ex, "History hydration failed; starting with an empty chat.");
            return new OperationsProbe(Reached: true, History: null, Errored: true);
        }
    }

    /// <summary>
    /// Applies a <see cref="WarmUpAgentAsync"/> result to the agent's <see cref="Connection"/> state. This
    /// is the chat gate — offline/connecting disables input. Must run on the render thread.
    /// </summary>
    private void ApplyAgentReachability(bool reached)
    {
        if (reached is false)
        {
            // Log the outage once, on entering offline — the loop probes repeatedly and we do not want a
            // line per attempt. (A mid-turn drop logs its own message and sets Offline before reaching here,
            // so this will not double-log that case.)
            if (this.Connection != ConnectionStatus.Offline)
            {
                this.Logger.LogInformation("Agent not reachable; retrying until it returns.");
            }

            this.Connection = ConnectionStatus.Offline;
            return;
        }

        // Coming back from an outage: one line to mark recovery (skipped on the first successful connect,
        // where the status is still Connecting rather than Offline).
        if (this.Connection == ConnectionStatus.Offline)
        {
            this.Logger.LogInformation("Agent reachable again.");
        }

        // Recovering from a mid-turn drop: update the stale "Reconnecting…" notice so it tells the user
        // their message was not delivered and should be resent (we never auto-resend — double-send risk).
        if (this.OfflineNoticeMessage is not null)
        {
            this.MarkOfflineNoticeReconnected();
        }

        this.Connection = ConnectionStatus.Online;
    }

    /// <summary>
    /// Applies a <see cref="ProbeOperationsAsync"/> result to the <see cref="OperationsConnection"/> state
    /// and, on the first clean reach, paints the hydrated transcript. The operations are SOFT — this never
    /// touches the chat gate. Must run on the render thread.
    /// </summary>
    private void ApplyOperationsProbe(OperationsProbe probe)
    {
        if (probe.Reached is false)
        {
            if (this.OperationsConnection != ConnectionStatus.Offline)
            {
                this.Logger.LogInformation(
                    "Custom operations not reachable; history/reset/remember are unavailable until they return.");
            }

            this.OperationsConnection = ConnectionStatus.Offline;
            return;
        }

        if (this.OperationsConnection == ConnectionStatus.Offline)
        {
            this.Logger.LogInformation("Custom operations reachable again.");
        }

        // Hydrate the stored transcript only on the FIRST successful reach, and never after the
        // user has started a turn (AddUserMessageAsync also sets InitialHydrationDone) — otherwise a late
        // or reconnect hydration would repaint prior history on top of the live conversation.
        if (probe.Errored is false && this.InitialHydrationDone is false)
        {
            this.ApplyHydratedHistory(probe.History);
        }

        this.InitialHydrationDone = true;
        this.OperationsConnection = ConnectionStatus.Online;
    }

    /// <summary>
    /// Rewrites the held "went offline" notice from its reconnecting text to a resend hint, now that the
    /// server is reachable again, and releases the reference.
    /// </summary>
    private void MarkOfflineNoticeReconnected()
    {
        ChatMessage notice = this.OfflineNoticeMessage!;
        TextContent? text = notice.Contents.OfType<TextContent>().FirstOrDefault();
        if (text is not null)
        {
            // Two things to get right here. (1) Be DEFINITE that no reply will arrive for this turn: a
            // response only streams back on the original request, which died with the connection; the
            // reconnect path just re-probes /history and never re-requests or delivers the lost turn's
            // reply, and we don't re-hydrate on reconnect. So "if there's no response" would be misleading —
            // there won't be one; resending is the only way forward. (2) HEDGE on receipt: the drop doesn't
            // tell us whether the server received (or even processed and stored) the turn before dying, so a
            // resend may duplicate it (we don't do idempotent dedup).
            text.Text = "The connection dropped during this message, so no reply will arrive for it. The "
                + "assistant may or may not have received it — resend to continue (if it did receive the "
                + "first one, resending could duplicate it in the assistant's history).";
        }

        ChatMessageItem.NotifyChanged(notice);
        this.OfflineNoticeMessage = null;
    }

    /// <summary>
    /// Paints the server's stored transcript into the chat so a freshly-opened client shows the whole
    /// conversation instead of starting blank. The server owns history (one durable context); this appends
    /// the minimal {role, text} projection returned by its <c>/history</c> endpoint.
    /// </summary>
    private void ApplyHydratedHistory(List<HistoryMessage>? history)
    {
        if (history is not { Count: > 0 })
        {
            return;
        }

        foreach (HistoryMessage item in history)
        {
            ChatRole role = (string.Equals(item.Role, "user", StringComparison.Ordinal) is true)
                ? ChatRole.User
                : ChatRole.Assistant;
            var message = new ChatMessage(role, item.Text);

            // Re-attach an inline image gallery the server reconstructed for this answer from the stored
            // show_images result — the same AdditionalProperties stash the live streaming path sets, so
            // ChatMessageItem renders the gallery beneath the hydrated answer exactly as it did live.
            if (item.Images is { Images.Count: > 0 } hydratedImages)
            {
                message.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                message.AdditionalProperties[ImagesPropertyKey] = hydratedImages;
            }

            this.Messages.Add(message);
        }

        // The server already holds this history, so it must NOT be re-sent as new input on the next
        // turn. Advance the delta checkpoint past the hydrated messages (BuildOutboundMessages sends
        // only the messages after statefulMessageCount).
        this.StatefulMessageCount = this.Messages.Count;
    }

    /// <summary>Starts (or restarts) the background auto-reconnect loop.</summary>
    private void StartConnectionLoop()
    {
        this.CancelConnectionLoop();
        this.ConnectionLoopCancellation = new CancellationTokenSource();
        this.ConnectionLoopTask = this.RunConnectionLoopAsync(this.ConnectionLoopCancellation.Token);
    }

    /// <summary>
    /// Runs both reachability checks with exponential backoff until BOTH are online, applying each result on
    /// the render thread. Re-probes only the check(s) not already online — so a healthy agent is never
    /// re-warmed on the operations' account (no steady-state warm-up heartbeat), and vice-versa. The first
    /// iteration runs immediately (local connects flip fast); failures back off. Runs in the background.
    /// </summary>
    private async Task RunConnectionLoopAsync(CancellationToken cancellationToken)
    {
        int delayMs = 0;
        while (cancellationToken.IsCancellationRequested is false)
        {
            if (delayMs > 0)
            {
                try
                {
                    await Task.Delay(delayMs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            bool agentReached = this.Connection == ConnectionStatus.Online
                || await this.WarmUpAgentAsync(cancellationToken);
            OperationsProbe? operationsProbe = this.OperationsConnection == ConnectionStatus.Online
                ? null
                : await this.ProbeOperationsAsync(cancellationToken);

            // CA1508: the analyzer wrongly concludes this is always false. The token can be cancelled during
            // the probe awaits (dispose, or a manual retry restarting the loop); the probes swallow that
            // cancellation, so this guard is what stops a stale result being applied to a torn-down component.
#pragma warning disable CA1508
            if (cancellationToken.IsCancellationRequested is true)
#pragma warning restore CA1508
            {
                return;
            }

            try
            {
                await this.InvokeAsync(() =>
                {
                    this.ApplyAgentReachability(agentReached);
                    if (operationsProbe is { } probe)
                    {
                        this.ApplyOperationsProbe(probe);
                    }

                    this.StateHasChanged();
                });
            }
            catch (ObjectDisposedException)
            {
                // The renderer/circuit was torn down (e.g. a prerender instance, or the user navigated away)
                // while the loop was running. Nothing more to do.
                return;
            }

            if (this.Connection == ConnectionStatus.Online && this.OperationsConnection == ConnectionStatus.Online)
            {
                return;
            }

            delayMs = (delayMs == 0) ? InitialRetryDelayMs : Math.Min(delayMs * 2, MaxRetryDelayMs);
        }
    }

    /// <summary>Manual "Retry now" from the offline banner: re-probe both back ends immediately, resuming auto-retry if either is still down.</summary>
    private async Task RetryNowAsync()
    {
        this.CancelConnectionLoop();
        if (this.Connection != ConnectionStatus.Online)
        {
            this.Connection = ConnectionStatus.Connecting;
        }

        bool agentReached = this.Connection == ConnectionStatus.Online
            || await this.WarmUpAgentAsync(CancellationToken.None);
        OperationsProbe? operationsProbe = this.OperationsConnection == ConnectionStatus.Online
            ? null
            : await this.ProbeOperationsAsync(CancellationToken.None);

        this.ApplyAgentReachability(agentReached);
        if (operationsProbe is { } probe)
        {
            this.ApplyOperationsProbe(probe);
        }

        if (this.Connection != ConnectionStatus.Online || this.OperationsConnection != ConnectionStatus.Online)
        {
            this.StartConnectionLoop();
        }
    }

    /// <summary>Cancels and disposes the current reconnect loop's token source, if any.</summary>
    private void CancelConnectionLoop()
    {
        if (this.ConnectionLoopCancellation is not null)
        {
            this.ConnectionLoopCancellation.Cancel();
            this.ConnectionLoopCancellation.Dispose();
            this.ConnectionLoopCancellation = null;
        }
    }

    /// <summary>
    /// True if <paramref name="exception"/> (or something in its inner chain) is a connection-level
    /// failure — an <see cref="HttpRequestException"/> with no HTTP status, or a socket error — i.e. the
    /// server could not be reached, as opposed to the server responding with an error.
    /// </summary>
    private static bool IsConnectionFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: null })
            {
                return true;
            }

            if (current is System.Net.Sockets.SocketException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether to show the first-run suggestion chips. Once the agent is online AND either the initial
    /// history has hydrated or the operations are confirmed offline (no history is coming). Waiting for
    /// hydration avoids a client with an existing conversation flashing the chips before the stored
    /// transcript paints; the operations-offline case still shows them so an operations outage is not a blank
    /// empty state while chat itself works.
    /// </summary>
    private bool ShowSuggestions => this.Connection == ConnectionStatus.Online
        && (this.InitialHydrationDone is true || this.OperationsConnection == ConnectionStatus.Offline);

    /// <summary>
    /// True once the first connectivity check has completed, so the <see cref="Connection"/> status is a
    /// real assessment rather than the starting default. Gates the connection banner / input-disabled state
    /// so neither the static prerender pass (which never runs the probe) nor the brief window before the
    /// first interactive probe returns paints a "Connecting…" banner that then flashes away on a healthy
    /// connect. Set only on the interactive render, after the initial probe.
    /// </summary>
    private bool InitialConnectDone { get; set; }

    /// <summary>
    /// True when a completed connectivity check says the client is not online — drives the connection banner
    /// and the input-disabled state. False until the first check returns (see <see cref="InitialConnectDone"/>),
    /// so a healthy startup never flashes a banner.
    /// </summary>
    private bool ConnectionPending => this.InitialConnectDone && this.Connection != ConnectionStatus.Online;

    /// <summary>CSS modifier class for the connection banner, by status.</summary>
    private string ConnectionCssClass => this.Connection switch
    {
        ConnectionStatus.Offline => "connection-offline",
        ConnectionStatus.Connecting => "connection-connecting",
        _ => "connection-online",
    };

    /// <summary>
    /// Minimal display projection returned by the server's history operation (<c>x-client-op: history</c>):
    /// a role (<c>"user"</c> / <c>"assistant"</c>) and the message text.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "Instantiated by System.Text.Json when deserializing the history operation's response.")]
    private sealed record HistoryMessage(string Role, string Text, ImageResult? Images = null);

    /// <summary>Result of one <see cref="ProbeOperationsAsync"/> attempt.</summary>
    /// <param name="Reached">True if the server responded at all (even with an empty history).</param>
    /// <param name="History">The hydrated transcript when the probe reached cleanly; otherwise null.</param>
    /// <param name="Errored">True if the server was reached but the history call itself failed.</param>
    private readonly record struct OperationsProbe(bool Reached, List<HistoryMessage>? History, bool Errored);

    /// <summary>
    /// Body of the "remember this grant permanently" request sent to the server's approvals operation
    /// (<c>x-client-op: approvals</c>): the gated tool's name and the exact arguments it was called with, as a JSON object. The
    /// server canonicalizes the arguments itself (so the stored rule matches the auto-approval predicate).
    /// Serialized camelCase.
    /// </summary>
    private sealed record PersistApprovalRequest(string ToolName, JsonElement Arguments);

    /// <summary>
    /// The server's reply to a persist-approval request: whether the grant was stored, and a reason when it
    /// was not (today only a human-only tool, which can never receive a durable grant).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "Instantiated by System.Text.Json when deserializing the /approvals response.")]
    private sealed record PersistApprovalResult(bool Persisted, string? Reason);

    /// <summary>Opens the "new conversation" confirmation card.</summary>
    private void ShowResetConfirm() => this.ResetConfirmVisible = true;

    /// <summary>Dismisses the "new conversation" confirmation card without resetting.</summary>
    private void CancelResetConfirm() => this.ResetConfirmVisible = false;

    /// <summary>
    /// Starts a new conversation: asks the server to archive its one durable context (POST <c>/reset</c>),
    /// then clears the local chat view to match. Durable memory (the <c>working/</c> files) and standing
    /// approvals live server-side outside the transcript and are untouched — only the running conversation is
    /// reset. Best-effort: a failed reset call is logged and the UI is left as-is, so the user is never shown
    /// an empty chat while the server still holds the old transcript.
    /// </summary>
    private async Task NewConversationAsync()
    {
        // Defensive: the confirm button is disabled while a response streams or a reset is already in
        // flight, but guard here too so a race can never start an archive that competes with a live turn's
        // snapshot write.
        if (this.ResetBlocked is true)
        {
            return;
        }

        this.ResetConfirmVisible = false;

        // Mark the reset in flight so sending a new prompt is blocked until the archive completes (a fresh
        // turn's write must not race the archive). Render now so the input disables during the round-trip.
        this.ResetInFlight = true;
        this.StateHasChanged();

        try
        {
            HttpClient httpClient = this.HttpClientFactory.CreateClient("agent");
            using HttpRequestMessage request = this.CreateAgentOpRequest(
                "reset", new StringContent("{}", Encoding.UTF8, "application/json"));

            using HttpResponseMessage response = await httpClient.SendAsync(request, CancellationToken.None);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            // A non-success reset (including a 409 when the archive lost a race to a concurrent turn) is
            // logged and the UI is left as-is, so the user is never shown an empty chat while the server
            // still holds the old transcript.
            this.Logger.LogWarning(ex, "New-conversation reset failed; keeping the current chat.");
            return;
        }
        finally
        {
            // Archive round-trip done (success or failure): re-enable sending.
            this.ResetInFlight = false;
        }

        // Server transcript archived — clear the local view to match. Cancel any in-flight stream first so a
        // late streamed update cannot repaint into the fresh conversation.
        if (this.CurrentResponseCancellation is not null)
        {
            await this.CurrentResponseCancellation.CancelAsync();
        }

        this.CurrentResponseMessage = null;
        this.PendingApproval = null;
        this.ApprovalRememberConfirmVisible = false;
        this.OfflineNoticeMessage = null;
        this.AgentState = null;
        this.Messages.Clear();
        this.StatefulMessageCount = 0;
        this.StateHasChanged();
    }

    private async Task AddUserMessageAsync(ChatMessage userMessage)
    {
        this.CancelAnyCurrentResponse();
        this.PendingApproval = null;
        this.ApprovalRememberConfirmVisible = false;

        // If the user starts a turn before the stored transcript has hydrated (operations slow or unreachable),
        // suppress a later hydration so it can't repaint prior history on top of this live conversation.
        this.InitialHydrationDone = true;

        this.Messages.Add(userMessage);
        if (this.ChatInput is not null)
        {
            await this.ChatInput.FocusAsync();
        }

        var responseText = new TextContent("");
        var responseMessage = new ChatMessage(ChatRole.Assistant, [responseText]);
        var cancellation = new CancellationTokenSource();
        this.CurrentResponseMessage = responseMessage;
        this.LastResponseActivityUtc = DateTimeOffset.UtcNow;
        this.CurrentResponseCancellation = cancellation;

        var outboundMessages = this.BuildOutboundMessages();
        await this.GetStreamingResponseAsync(outboundMessages, responseText, responseMessage,
            cancellation.Token);

        // Finalize THIS turn only if it was not superseded. If the user sent another message while this
        // reply was streaming, CancelAnyCurrentResponse cancelled our token AND already committed this
        // turn's partial message — so re-adding would duplicate it, and clearing the shared field would
        // null the newer turn's in-progress pointer (and later commit a null). Guarding on our own
        // captured token lets each turn finalize only its own state. Commit the LOCAL responseMessage,
        // never this.CurrentResponseMessage, which a newer turn may have reassigned.
        if (cancellation.IsCancellationRequested is false)
        {
            this.Messages.Add(responseMessage);
            this.StatefulMessageCount = this.ActiveSession is not null ? this.Messages.Count : 0;
            this.CurrentResponseMessage = null;
        }
    }

    private async Task SendSuggestionAsync(string text)
    {
        await this.AddUserMessageAsync(new ChatMessage(ChatRole.User, text));
    }

    /// <summary>
    /// Answers a pending <see cref="ToolApprovalRequestContent"/> with a one-time approve (<c>true</c>) or a
    /// deny (<c>false</c>), resuming the paused server run via <see cref="SendApprovalResponseAsync"/>. A deny
    /// tags the follow-up message with the inline "denied" marker. The durable "Always approve" path is
    /// <see cref="AlwaysApproveAsync"/>.
    /// </summary>
    private async Task RespondToApprovalAsync(bool approved)
    {
        var request = this.PendingApproval;
        if (request is null)
        {
            return;
        }

        this.PendingApproval = null;
        this.ApprovalRememberConfirmVisible = false;

        AdditionalPropertiesDictionary? markerProperties = null;
        if (approved is false)
        {
            // Subtle inline marker so the user sees the gate acted. The denied tool's result is a
            // plain "rejected" string the thread would not otherwise surface, so tag the follow-up
            // assistant message; ChatMessageItem renders a muted "denied" line above its text.
            var deniedTool = (request.ToolCall as FunctionCallContent)?.Name ?? "the tool";
            markerProperties = new AdditionalPropertiesDictionary { [ToolDeniedPropertyKey] = deniedTool };
        }

        await this.SendApprovalResponseAsync(request, approved, markerProperties);
    }

    /// <summary>Opens the deliberate "remember permanently" confirmation step for the pending approval.</summary>
    private void ShowRememberConfirm() => this.ApprovalRememberConfirmVisible = true;

    /// <summary>
    /// Confirms "Always approve": persists a standing grant for the pending tool call, then approves this
    /// call. Persisting is best-effort — the call is approved either way, and remembering is a convenience,
    /// never a gate. A subtle inline marker tells the user whether the grant was saved.
    /// </summary>
    private async Task AlwaysApproveAsync()
    {
        var request = this.PendingApproval;
        if (request is null)
        {
            return;
        }

        var toolCall = request.ToolCall as FunctionCallContent;

        // Clear the panel immediately so it does not linger during the persist round-trip.
        this.PendingApproval = null;
        this.ApprovalRememberConfirmVisible = false;
        this.StateHasChanged();

        // Write the durable grant server-side (where the auto-approval predicate reads it). Only an
        // exact-args tool call can be remembered; a request without a FunctionCallContent falls back to
        // approve-once.
        var remembered = toolCall is not null && await this.PersistApprovalAsync(toolCall);

        var toolName = toolCall?.Name ?? "the tool";
        var markerProperties = new AdditionalPropertiesDictionary
        {
            [(remembered is true) ? ToolRememberedPropertyKey : ToolRememberFailedPropertyKey] = toolName,
        };

        await this.SendApprovalResponseAsync(request, approved: true, markerProperties);
    }

    /// <summary>
    /// Asks the server to persist a standing approval for <paramref name="toolCall"/> via
    /// <c>POST /approvals</c>. The server re-checks the human-only floor and canonicalizes the arguments
    /// itself (so the stored rule matches the auto-approval predicate). Returns <see langword="true"/> only
    /// when the server reports the grant was saved; any failure or a non-persistable tool returns
    /// <see langword="false"/> so the caller falls back to a one-time approval.
    /// </summary>
    private async Task<bool> PersistApprovalAsync(FunctionCallContent toolCall)
    {
        try
        {
            HttpClient httpClient = this.HttpClientFactory.CreateClient("agent");

            // Serialize the arguments to a JSON object so the record carries a JsonElement (symmetric with
            // the server's request shape); the server canonicalizes it with the same ArgCanonicalizer the
            // auto-approval predicate uses. A call with no arguments becomes an empty object.
            JsonElement arguments = JsonSerializer.SerializeToElement(
                toolCall.Arguments ?? new Dictionary<string, object?>(StringComparer.Ordinal));
            var body = new PersistApprovalRequest(toolCall.Name, arguments);
            using HttpRequestMessage request = this.CreateAgentOpRequest("approvals", JsonContent.Create(body));
            using HttpResponseMessage response = await httpClient.SendAsync(request, CancellationToken.None);

            if (response.IsSuccessStatusCode is false)
            {
                this.Logger.LogWarning(
                    "Saving standing approval for {Tool} returned {Status}; approving once instead.",
                    toolCall.Name,
                    response.StatusCode);
                return false;
            }

            PersistApprovalResult? result = await response.Content.ReadFromJsonAsync<PersistApprovalResult>(CancellationToken.None);
            if (result is { Persisted: true })
            {
                this.Logger.LogInformation("Standing approval saved for {Tool}.", toolCall.Name);
                return true;
            }

            this.Logger.LogInformation(
                "Standing approval not saved for {Tool} (reason: {Reason}); approving once instead.",
                toolCall.Name,
                result?.Reason ?? "unknown");
            return false;
        }
        catch (Exception ex)
        {
            this.Logger.LogWarning(ex, "Could not save standing approval for {Tool}; approving once instead.", toolCall.Name);
            return false;
        }
    }

    /// <summary>
    /// Resumes the paused server run with an approve/deny decision, tagging the follow-up assistant message
    /// with an optional inline marker (denied / remembered / couldn't-remember).
    /// </summary>
    /// <remarks>
    /// We build the AG-UI <c>Resume</c> entry OURSELVES and hand it to the client via
    /// <see cref="ChatOptions.RawRepresentationFactory"/> (see <see cref="GetStreamingResponseAsync"/>),
    /// sending NO approval message. Under our server-authoritative (delta) model the thread does not carry
    /// the original request, so the client-side approval pipeline cannot pair request↔response and silently
    /// drops a response sent as a message (empty Resume on the wire — verified by probe). The resume's
    /// <c>InterruptId</c> is the request's and the payload echoes the full tool call, which is what the
    /// server's AG-UI decode uses to rebuild the approval without the history we no longer replay. Approve
    /// vs. deny is carried by <c>Approved</c>.
    /// </remarks>
    private async Task SendApprovalResponseAsync(
        ToolApprovalRequestContent request,
        bool approved,
        AdditionalPropertiesDictionary? markerProperties)
    {
        var responseText = new TextContent("");
        var responseMessage = new ChatMessage(ChatRole.Assistant, [responseText]);
        this.LastResponseActivityUtc = DateTimeOffset.UtcNow;
        if (markerProperties is not null)
        {
            responseMessage.AdditionalProperties = markerProperties;
        }

        this.CurrentResponseMessage = responseMessage;
        var cancellation = new CancellationTokenSource();
        this.CurrentResponseCancellation = cancellation;

        var toolCall = request.ToolCall as FunctionCallContent;

        // Build the AG-UI Resume ourselves. Its InterruptId is the request id the paused run is keyed by,
        // and its payload echoes the tool call so the server can rebuild the approval without replayed history.
        var payload = JsonSerializer.SerializeToElement(new AGUIToolApprovalResumePayload
        {
            Approved = approved,
            ToolCall = toolCall is null
                ? null
                : new AGUIToolCallInfo
                {
                    CallId = toolCall.CallId,
                    Name = toolCall.Name,
                    Arguments = toolCall.Arguments,
                },
        });
        List<AGUIResume> resume =
        [
            new() { InterruptId = request.RequestId, Status = ResumeStatus.Resolved, Payload = payload },
        ];

        this.Logger.LogInformation(
            "Resuming approval (caller-supplied Resume): reqId={ReqId} approved={Approved}",
            request.RequestId,
            approved);

        await this.GetStreamingResponseAsync(
            [],
            responseText,
            responseMessage,
            cancellation.Token,
            resume);

        // Same supersede guard as AddUserMessageAsync (see the comment there for the full rationale):
        // finalize only if a newer turn did not cancel this one, and commit the LOCAL responseMessage.
        if (cancellation.IsCancellationRequested is false)
        {
            this.Messages.Add(responseMessage);
            this.StatefulMessageCount = this.ActiveSession is not null ? this.Messages.Count : 0;
            this.CurrentResponseMessage = null;
        }
    }

    private async Task GetStreamingResponseAsync(
        IEnumerable<ChatMessage> outboundMessages,
        TextContent responseText,
        ChatMessage responseMessage,
        CancellationToken cancellationToken,
        List<AGUIResume>? resume = null)
    {
        this.Logger.LogInformation("Streaming started");
        var updateCount = 0;

        // Server-authoritative history. Pass a null AgentSession so the client-side AGUIChatClient session does
        // NOT replay its accumulated thread into RunAgentInput. outboundMessages is already delta-only
        // (statefulMessageCount advances off the non-null this.ActiveSession FIELD), so only the new user turn
        // goes on the wire and the server's one durable context (store=false) supplies the rest of history.
        //
        // RawRepresentationFactory pins a STABLE AG-UI thread id (this.ThreadId) on every turn — including the
        // approval-resume turn, since RespondToApprovalAsync funnels through here. We do this via a RunAgentInput
        // rather than a ConversationId on purpose: a non-null ConversationId puts ChatClientAgent in
        // service-managed mode, which requires the inner client to echo a ConversationId back — but AGUIChatClient
        // deliberately nulls ConversationId on every response (#4869), so that path throws "Service did not return
        // a valid conversation id". Driving the wire format through RawRepresentationFactory -> RunAgentInput is
        // the AGUI.Client-sanctioned seam (see AGUIClientInternalKeys' own guidance) and sets RunAgentInput.ThreadId
        // directly (its top-priority thread-id source). Verified against Microsoft.Agents.AI 1.26 / AGUI.Client
        // 1.0.1: delta-only, thread id forwarded, no service-managed crash. The stable thread id is what lets
        // approval binding (PR #8641, kept ON) resume onto the same durable server session that holds the
        // pending-approval binding.
        //
        // `resume` (non-null only on the approval round-trip from RespondToApprovalAsync) is handed to the client
        // via the SAME RawRepresentationFactory -> RunAgentInput seam. AGUIChatClient.BuildRunAgentInput treats a
        // caller-supplied Resume as authoritative (its callerSuppliedResume path) and forwards it straight to the
        // transport, deliberately BYPASSING the client-side approval pipeline (ApprovalResponseBindingChatClient ->
        // FunctionInvokingChatClient). That pipeline pairs a ToolApprovalRequest/Response by matching them in the
        // message list, which our server-authoritative (delta) model does not carry — so sending the response AS A
        // MESSAGE yields an empty Resume (the resume silently dropped; verified by probe). Building the AGUIResume
        // ourselves and sending it here (with NO approval message) is the AGUI-sanctioned way to resume without
        // replaying history. See RespondToApprovalAsync.
        var runOptions = new ChatClientAgentRunOptions(new ChatOptions
        {
            RawRepresentationFactory = _ => new RunAgentInput { ThreadId = this.ThreadId, Resume = resume },
        });

        try
        {
            await foreach (var update in this.Agent.RunStreamingAsync(
                outboundMessages,
                (AgentSession?)null,
                runOptions,
                cancellationToken: cancellationToken))
            {
                if (update is null)
                {
                    continue;
                }

                updateCount++;

                // Refresh the activity timestamp on every streamed update so the reset-block timeout
                // measures inactivity, not elapsed time: a long but live turn keeps this fresh (reset stays
                // disabled), while a hung turn stops updating it and the timeout re-enables reset.
                this.LastResponseActivityUtc = DateTimeOffset.UtcNow;
                this.Logger.LogDebug(
                    "Update #{Count}: role={Role} contents=[{Types}] props=[{Props}] text={Text}",
                    updateCount,
                    update.Role,
                    string.Join(", ", update.Contents.Select(c => c is DataContent dc
                        ? $"Data({dc.MediaType},{dc.Data.Length}b)"
                        : c.GetType().Name)),
                    string.Join(", ", update.AdditionalProperties?.Keys ?? []),
                    update.Text is { Length: > 0 } t ? $"\"{t[..Math.Min(t.Length, 40)]}\"" : "-");

                foreach (var fc in update.Contents.OfType<FunctionCallContent>())
                {
                    this.Logger.LogInformation("  Tool call: {Name} ({CallId})", fc.Name, fc.CallId);
                }

                // A gated tool surfaces a ToolApprovalRequestContent instead of executing. Stash it
                // and show Approve/Deny with the tool's arguments; the turn ends here
                // (RUN_FINISHED outcome=interrupt).
                var approvalRequest = update.Contents.OfType<ToolApprovalRequestContent>().FirstOrDefault();
                if (approvalRequest is not null)
                {
                    this.PendingApproval = approvalRequest;
                    var fcc = approvalRequest.ToolCall as FunctionCallContent;
                    this.Logger.LogInformation(
                        "  >>> Approval request received: reqId={ReqId} tool={Tool} args={Args}",
                        approvalRequest.RequestId,
                        fcc?.Name,
                        fcc?.Arguments is { Count: > 0 } a
                            ? string.Join("; ", a.Select(kv => $"{kv.Key}={FormatArgValue(kv.Value)}"))
                            : "(none)");
                    this.StateHasChanged();
                    continue;
                }

                var handledToolResult = false;
                foreach (var fr in update.Contents.OfType<FunctionResultContent>())
                {
                    this.Logger.LogInformation("  Tool result ({CallId}): {Result}", fr.CallId, fr.Result);
                    if (this.TryExtractStateFromToolResult(fr) is true)
                    {
                        handledToolResult = true;
                        continue;
                    }

                    if (this.TryAttachImagesFromToolResult(fr, responseMessage) is true)
                    {
                        handledToolResult = true;
                    }
                }

                if (handledToolResult is true)
                {
                    this.StateHasChanged();
                    continue;
                }

                // State snapshot: full replacement of agent state.
                // Primary signal: DataContent with application/json (confirmed by integration tests).
                // Fallback: AdditionalProperties flag set by AGUIChatClient.
                var isSnapshot = update.Contents.Any(c => c is DataContent { MediaType: "application/json" })
                    || (update.AdditionalProperties?.TryGetValue("is_state_snapshot", out var snapshotFlag) is true && snapshotFlag is true);
                if (isSnapshot is true)
                {
                    this.ProcessStateSnapshot(update);
                    this.StateHasChanged();
                    continue;
                }

                // State delta: JSON Patch applied to current agent state.
                // is_state_delta is untested in the NuGet package — use media type as primary signal.
                var isDelta = update.Contents.Any(c => c is DataContent { MediaType: "application/json-patch+json" })
                    || (update.AdditionalProperties?.TryGetValue("is_state_delta", out var deltaFlag) is true && deltaFlag is true);
                if (isDelta is true)
                {
                    this.ProcessStateDelta(update);
                    this.StateHasChanged();
                    continue;
                }

                // Error from agent
                var errorContent = update.Contents.OfType<ErrorContent>().FirstOrDefault();
                if (errorContent is not null)
                {
                    this.Logger.LogError(
                        "Agent returned error (code: {Code}): {Message}",
                        errorContent.ErrorCode,
                        errorContent.Message);
                    responseText.Text = $"Sorry, something went wrong: {errorContent.Message}";
                    ChatMessageItem.NotifyChanged(responseMessage);
                    this.StateHasChanged();
                    break;
                }

                // Regular streamed text
                if (update.Role == ChatRole.Assistant && update.Text is { Length: > 0 })
                {
                    // S1643: not a local string build — this appends each streamed delta to the bound
                    // TextContent so the UI repaints incrementally; a StringBuilder assigned once after the
                    // loop would defeat live streaming.
#pragma warning disable S1643
                    responseText.Text += update.Text;
#pragma warning restore S1643
                    ChatMessageItem.NotifyChanged(responseMessage);
                }

                this.StateHasChanged();
                await Task.Delay(50, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // S6667: cancellation is normal control flow (the user sent a new turn / reset), not an error —
            // we deliberately log it at Information without the exception, since no stack trace is useful.
#pragma warning disable S6667
            this.Logger.LogInformation("Streaming cancelled after {Count} updates", updateCount);
#pragma warning restore S6667
        }
        catch (JsonException ex)
        {
            this.Logger.LogError(
                ex,
                "JSON parsing error at position {Position}. The agent returned an unexpected format.",
                ex.BytePositionInLine);
            responseText.Text = "Error: The agent returned an unexpected response format.";
            ChatMessageItem.NotifyChanged(responseMessage);
            this.StateHasChanged();
        }
        // S2139: deliberately log-with-local-context-then-rethrow — updateCount is only known here and is
        // worth capturing at the failure site; the rethrow lets the Blazor circuit handle/surface it. The
        // pragma spans the catch because the analyzer anchors the finding on the catch clause itself.
#pragma warning disable S2139
        catch (Exception ex)
        {
            // A connection-level failure means the server went down (not ready at startup, or killed
            // mid-turn — common in dev). It is expected and handled, so log it quietly (no stack) and switch
            // to offline/reconnect, rather than logging a full error stack and tearing down the circuit.
            if (IsConnectionFailure(ex) is true)
            {
#pragma warning disable S6667
                this.Logger.LogInformation(
                    "Streaming lost the connection to the agent server after {Count} updates; going offline to reconnect.",
                    updateCount);
#pragma warning restore S6667
                responseText.Text = "Lost connection to the assistant while sending this message. Reconnecting…";
                this.OfflineNoticeMessage = responseMessage;
                ChatMessageItem.NotifyChanged(responseMessage);
                this.Connection = ConnectionStatus.Offline;
                this.StartConnectionLoop();
                this.StateHasChanged();
                return;
            }

            this.Logger.LogError(ex, "Unexpected streaming error after {Count} updates", updateCount);
            throw;
        }
#pragma warning restore S2139
        finally
        {
            this.Logger.LogInformation("Streaming exited after {Count} updates", updateCount);
        }
    }

    /// <summary>
    /// Extracts agent state from a tool result. AGUI.Client v0.0.4 does not emit
    /// DataContent for STATE_SNAPSHOT/STATE_DELTA — state arrives as tool results instead.
    /// Handles two formats: full AgentState JSON object (create_plan) and RFC 6902 patch
    /// array (update_plan_step).
    /// </summary>
    private bool TryExtractStateFromToolResult(FunctionResultContent fr)
    {
        var json = fr.Result switch
        {
            string s => s,
            JsonElement je => je.GetRawText(),
            _ => fr.Result?.ToString()
        };

        if (string.IsNullOrWhiteSpace(json) is true)
        {
            return false;
        }

        // A generic (non plan-state) tool result — e.g. record_note, or the deny sentinel
        // "Tool call invocation rejected." — is a plain string, not JSON. Only create_plan /
        // update_plan_step return a JSON object/array, so skip anything that is not JSON-shaped
        // rather than parsing it and catching a JsonReaderException on every generic tool.
        var jsonSpan = json.AsSpan().TrimStart();
        if (jsonSpan.IsEmpty || (jsonSpan[0] != '{' && jsonSpan[0] != '['))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("steps", out _))
            {
                var state = JsonSerializer.Deserialize<AgentState>(json, JsonOptions);
                if (state is not null)
                {
                    this.AgentState = state;
                    this.Logger.LogInformation(
                        "State snapshot from tool result ({CallId}): {Count} steps",
                        fr.CallId,
                        state.Steps.Count);
                    return true;
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Array && this.AgentState is not null)
            {
                var patches = JsonSerializer.Deserialize<List<JsonPatchOperation>>(json, JsonOptions);
                if (patches is { Count: > 0 })
                {
                    this.ApplyJsonPatch(patches);
                    this.Logger.LogInformation(
                        "State delta from tool result ({CallId}): {Count} ops",
                        fr.CallId,
                        patches.Count);
                    return true;
                }
            }
        }
        catch (JsonException ex)
        {
            this.Logger.LogWarning(ex, "Could not parse tool result as state ({CallId})", fr.CallId);
        }

        return false;
    }

    /// <summary>
    /// Detects a <c>show_images</c> tool result and, if found, stashes its <see cref="ImageResult"/> on the
    /// assistant message that produced it so <c>ChatMessageItem</c> renders the gallery beneath that answer.
    /// An image result is a JSON object with an <c>images</c> array — distinct from the plan snapshot
    /// (<c>steps</c>) and the plan delta (a bare array), so it does not collide with
    /// <see cref="TryExtractStateFromToolResult"/>. Returns <see langword="true"/> when images were attached.
    /// </summary>
    private bool TryAttachImagesFromToolResult(FunctionResultContent fr, ChatMessage responseMessage)
    {
        var json = fr.Result switch
        {
            string s => s,
            JsonElement je => je.GetRawText(),
            _ => fr.Result?.ToString()
        };

        if (string.IsNullOrWhiteSpace(json) is true)
        {
            return false;
        }

        // Only an image result is a JSON object; skip anything that is not object-shaped (plan deltas are
        // arrays, generic tool results are plain strings) rather than parsing and catching on every result.
        var jsonSpan = json.AsSpan().TrimStart();
        if (jsonSpan.IsEmpty || jsonSpan[0] != '{')
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || doc.RootElement.TryGetProperty("images", out _) is false)
            {
                return false;
            }

            var result = JsonSerializer.Deserialize<ImageResult>(json, JsonOptions);
            if (result is { Images.Count: > 0 })
            {
                responseMessage.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                responseMessage.AdditionalProperties[ImagesPropertyKey] = result;
                this.Logger.LogInformation(
                    "Images from tool result ({CallId}): {Count}",
                    fr.CallId,
                    result.Images.Count);
                ChatMessageItem.NotifyChanged(responseMessage);
                return true;
            }
        }
        catch (JsonException ex)
        {
            this.Logger.LogWarning(ex, "Could not parse tool result as images ({CallId})", fr.CallId);
        }

        return false;
    }

    private void ProcessStateSnapshot(AgentResponseUpdate update)
    {
        foreach (var content in update.Contents)
        {
            if (content is DataContent dc && string.Equals(dc.MediaType, "application/json", StringComparison.Ordinal))
            {
                var state = JsonSerializer.Deserialize<AgentState>(dc.Data.Span, JsonOptions);
                if (state is not null)
                {
                    this.AgentState = state;
                    this.Logger.LogInformation(
                        "State snapshot received: {Count} steps",
                        state.Steps.Count);
                }
            }
        }
    }

    private void ProcessStateDelta(AgentResponseUpdate update)
    {
        if (this.AgentState is null)
        {
            return;
        }

        foreach (var content in update.Contents)
        {
            if (content is DataContent dc
                && string.Equals(dc.MediaType, "application/json-patch+json", StringComparison.Ordinal))
            {
                var patches = JsonSerializer.Deserialize<List<JsonPatchOperation>>(dc.Data.Span, JsonOptions);
                if (patches is not null)
                {
                    this.ApplyJsonPatch(patches);
                }
            }
        }
    }

    /// <summary>
    /// Applies RFC 6902 replace operations to the current agent state.
    /// Supports /steps/{index}/status and /steps/{index}/description paths.
    /// </summary>
    private void ApplyJsonPatch(List<JsonPatchOperation> operations)
    {
        foreach (var op in operations)
        {
            if (string.Equals(op.Op, "replace", StringComparison.OrdinalIgnoreCase) is false)
            {
                continue;
            }

            var segments = op.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length != 3
                || string.Equals(segments[0], "steps", StringComparison.Ordinal) is false
                || int.TryParse(segments[1], out var index) is false)
            {
                continue;
            }

            if (this.AgentState is null
                || index < 0
                || index >= this.AgentState.Steps.Count)
            {
                continue;
            }

            var step = this.AgentState.Steps[index];

            if (string.Equals(segments[2], "status", StringComparison.Ordinal) is true)
            {
                var statusStr = op.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? op.Value.GetString()
                    : null;
                step.Status = string.Equals(statusStr, "completed", StringComparison.OrdinalIgnoreCase) is true
                    ? StepStatus.Completed
                    : StepStatus.Pending;

                this.Logger.LogInformation(
                    "Step {Index} status → {Status}",
                    index,
                    step.Status);
            }
            else if (string.Equals(segments[2], "description", StringComparison.Ordinal) is true)
            {
                step.Description = op.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? op.Value.GetString() ?? ""
                    : "";
            }
        }
    }

    private IEnumerable<ChatMessage> BuildOutboundMessages()
    {
        // Send only the messages added since the last stateful checkpoint
        return this.Messages.Skip(this.StatefulMessageCount);
    }

    // DESIGN TRIP-WIRE — read before adding a "stop generating" control. This commits the in-flight
    // turn's partial message to the transcript BEFORE cancelling, and the turn-finalize tails in
    // AddUserMessageAsync / SendApprovalResponseAsync deliberately SKIP their own commit when their
    // token was cancelled (so a superseded turn neither double-adds nor nulls the newer turn's
    // in-progress pointer). Those two facts are coupled: a stop control must route through here (or
    // otherwise commit the partial itself). Cancelling this.CurrentResponseCancellation directly would
    // cancel the token — making the tail skip — WITHOUT committing the partial, silently dropping the
    // user's partial reply.
    private void CancelAnyCurrentResponse()
    {
        if (this.CurrentResponseMessage is not null)
        {
            this.Messages.Add(this.CurrentResponseMessage);
        }

        this.CurrentResponseCancellation?.Cancel();
        this.CurrentResponseMessage = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        this.CancelConnectionLoop();
        if (this.ConnectionLoopTask is not null)
        {
            try
            {
                // VSTHRD003: awaiting our own loop Task during async disposal is safe — it was started via
                // this component's dispatcher, it is already cancelled (CancelConnectionLoop above), and it
                // never blocks synchronously on the Dispatcher, so it cannot deadlock. Blazor Server has no
                // JoinableTaskFactory to route this through.
#pragma warning disable VSTHRD003
                await this.ConnectionLoopTask;
#pragma warning restore VSTHRD003
            }
            catch (OperationCanceledException)
            {
                // Expected: the loop unwinds via its cancellation token.
            }
        }

        if (this.CurrentResponseCancellation is not null)
        {
            await this.CurrentResponseCancellation.CancelAsync();
            this.CurrentResponseCancellation.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>Reachability of the agent server, driving the connection banner and input gating.</summary>
internal enum ConnectionStatus
{
    /// <summary>A reachability attempt is in flight (initial load or a manual retry).</summary>
    Connecting,

    /// <summary>The server responded; chat is usable.</summary>
    Online,

    /// <summary>The server could not be reached; the banner offers retry and a loop reconnects.</summary>
    Offline,
}
