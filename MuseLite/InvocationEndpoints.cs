using Microsoft.Agents.AI;
using MyAssistant.ApprovalPolicy;
using MyAssistant.DurableContext;
using System.Text.Json;

namespace MuseLite;

#pragma warning disable MAAI001 // AgentSessionStoreKey is an experimental harness API.

/// <summary>
/// The server's custom durable-context operations — server-authoritative history hydration, the
/// memory-preserving reset, and the durable approval-persist write path — multiplexed onto the one
/// gateway-legal route via an <c>x-client-op</c> header, plus the platform readiness probe.
/// </summary>
/// <remarks>
/// The Foundry hosted-agent gateway is protocol-only: it forwards traffic to the container only on the
/// declared <c>/invocations</c> route (plus its own <c>/readiness</c> probe) and refuses arbitrary sibling
/// routes (e.g. <c>GET /history</c>) at the edge. So these three operations cannot be their own routes when
/// hosted; instead the client POSTs to <c>/invocations</c> with an <c>x-client-op</c> header (an
/// <c>x-client-*</c> name the gateway guarantees to forward) and <see cref="TryDispatchClientOpAsync"/>
/// answers it in middleware, before any AG-UI or platform-payload parsing runs. The same path works
/// identically against the local server, so one client call shape covers both environments.
/// </remarks>
internal static class InvocationEndpoints
{
    /// <summary>
    /// Maps the platform <c>GET /readiness</c> probe. Without it the Foundry Hosted Agents platform's
    /// health check hits nothing and invokes fail with 424 session_not_ready, even though the app is fine.
    /// Harmless locally.
    /// </summary>
    /// <param name="app">The web application to map the probe onto.</param>
    public static void MapReadinessProbe(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/readiness", () => Results.Ok(new { status = "ready" }));
    }

    /// <summary>
    /// Dispatches a request carrying an <c>x-client-op</c> header to the matching custom operation,
    /// writing the response and short-circuiting the pipeline. A request without the header is not this
    /// method's concern and is left for the rest of the pipeline. This MUST run before the Foundry payload
    /// adapter: that adapter rejects (400) any JSON object body lacking <c>messages</c>/<c>input</c>/
    /// <c>message</c>, which is exactly the shape of the approvals op body.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="server">The composed server (history reader, session store, approval store, policy).</param>
    /// <param name="jsonOptions">Serializer options for JSON responses and request-body binding.</param>
    /// <returns>
    /// <see langword="true"/> when the request carried an <c>x-client-op</c> header and the response was
    /// written here (the caller must stop); <see langword="false"/> when the request should continue.
    /// </returns>
    public static async Task<bool> TryDispatchClientOpAsync(HttpContext context, AssistantServer server, JsonSerializerOptions jsonOptions)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(jsonOptions);

        string? op = context.Request.Headers["x-client-op"].FirstOrDefault();
        if (op is null)
        {
            return false;
        }

        switch (op)
        {
            case "history":
                await WriteHistoryAsync(context, server, jsonOptions).ConfigureAwait(false);
                return true;
            case "reset":
                await ResetAsync(context, server).ConfigureAwait(false);
                return true;
            case "approvals":
                await PersistApprovalAsync(context, server, jsonOptions).ConfigureAwait(false);
                return true;
            default:
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return true;
        }
    }

    /// <summary>
    /// Writes the server-authoritative durable transcript. The one durable context lives on the server; a
    /// freshly opened client sends this on load to paint the whole conversation instead of starting blank.
    /// Reading is delegated to the shared <see cref="DurableHistoryReader"/>, which deserializes the stored
    /// snapshot with a dummy (no-model) client and projects it to {role, text, images}. An empty list means
    /// no context yet.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="server">The composed server.</param>
    /// <param name="jsonOptions">Serializer options for the JSON response.</param>
    private static async Task WriteHistoryAsync(HttpContext context, AssistantServer server, JsonSerializerOptions jsonOptions)
    {
        IReadOnlyList<HistoryMessage> history = await server.HistoryReader
            .ReadAsync(context.RequestAborted)
            .ConfigureAwait(false);
        await context.Response.WriteAsJsonAsync(history, jsonOptions, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Memory-preserving "new conversation" reset. Archives the one durable context (timestamped, under
    /// context/) and leaves no current snapshot, so the next chat turn starts an empty conversation. This
    /// resets ONLY the running transcript: the user-editable working/ store (identity/preferences/memories +
    /// standing approval rules) is a SEPARATE directory and is left untouched. The AG-UI host loads the
    /// session from the store on every request (no in-process session cache), so the archive takes effect on
    /// the very next turn with no restart. DeleteSessionAsync archives under the store's single-writer guard;
    /// the session-store key is ignored by MyAgentSessionStore (one fixed context), so any non-empty id
    /// works — but it must NOT be a reserved transient key or the delete is a no-op.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="server">The composed server.</param>
    private static async Task ResetAsync(HttpContext context, AssistantServer server)
    {
        await server.SessionStore
            .DeleteSessionAsync(server.BaseAgent, new AgentSessionStoreKey("reset"), context.RequestAborted)
            .ConfigureAwait(false);
        context.Response.StatusCode = StatusCodes.Status200OK;
    }

    /// <summary>
    /// Durable "remember this approval permanently" write path for the client's approval UI. An in-process
    /// host could write grants directly; the client is a separate process, so it POSTs here after the user confirms a
    /// standing grant. This ONLY persists the rule — approving the current call is a separate action the
    /// client still does over the AG-UI resume. The SAME ApprovalRuleStore backs the auto-approval predicate,
    /// so a grant written here is honored silently on the next matching call. Two safeguards: (1) the
    /// human-only floor is re-checked server-side so a raw request can never persist a grant for a tool that
    /// must always be a fresh human decision; (2) the SERVER canonicalizes the arguments with the same
    /// ArgCanonicalizer the predicate uses, so the stored rule is guaranteed to match (client-side
    /// canonicalization could drift and silently never match).
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="server">The composed server.</param>
    /// <param name="jsonOptions">Serializer options for the request body and JSON response.</param>
    private static async Task PersistApprovalAsync(HttpContext context, AssistantServer server, JsonSerializerOptions jsonOptions)
    {
        PersistApprovalRequest? request = await context.Request
            .ReadFromJsonAsync<PersistApprovalRequest>(jsonOptions, context.RequestAborted)
            .ConfigureAwait(false);
        if (request is null || string.IsNullOrWhiteSpace(request.ToolName) is true)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (server.HumanOnlyPolicy.IsPersistable(request.ToolName) is false)
        {
            await context.Response
                .WriteAsJsonAsync(new PersistApprovalResult(false, "human-only"), jsonOptions, context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        // Arguments arrive as the raw JSON object the client received in the approval request. Deserialize to
        // the same Dictionary<string, object?> shape the predicate canonicalizes (values become JsonElement,
        // exactly as FunctionCallContent.Arguments does), so the canonical forms match. A tool with no
        // arguments canonicalizes to "{}".
        Dictionary<string, object?>? arguments = request.Arguments.ValueKind == JsonValueKind.Object
            ? request.Arguments.Deserialize<Dictionary<string, object?>>(jsonOptions)
            : null;
        string canonicalArgs = ArgCanonicalizer.Canonicalize(arguments);
        await server.ApprovalStore.AddRuleAsync(request.ToolName, canonicalArgs).ConfigureAwait(false);
        await context.Response
            .WriteAsJsonAsync(new PersistApprovalResult(true, null), jsonOptions, context.RequestAborted)
            .ConfigureAwait(false);
    }

    // Body of the "remember this grant permanently" request from the client's approval UI. Carries the gated
    // tool's name and the exact arguments it was called with; the server canonicalizes the arguments (so the
    // written rule matches the auto-approval predicate, which canonicalizes the same way) and writes a durable
    // rule. Serialized camelCase ({toolName, arguments}).
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "Instantiated by System.Text.Json when deserializing the approvals request body.")]
    private sealed record PersistApprovalRequest(string ToolName, JsonElement Arguments);

    // Result of a persist-approval request. Persisted is false (with a reason) when the grant could not be
    // stored — today only because the tool is on the human-only floor, which can never receive a durable
    // grant. The client treats a non-persisted result as "approve once" and tells the user it was not saved.
    private sealed record PersistApprovalResult(bool Persisted, string? Reason);
}
