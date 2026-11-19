using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;

namespace MyAssistant.DurableContext;

/// <summary>
/// Recovery for a poisoned server-side conversation pointer. When the chat service runs in
/// stateful mode (OpenAI/Azure Responses API), each turn is chained to the prior one by a
/// <c>previous_response_id</c> carried as the session's top-level <c>conversationId</c>. If that
/// stored response is gone server-side (expiry/eviction, or a turn that failed to persist), every
/// subsequent turn re-sends the dead id and fails with HTTP 400 <c>previous_response_not_found</c>,
/// leaving the session permanently unusable.
/// </summary>
/// <remarks>
/// The fix exploits a verified fact about the harness session (confirmed from a live
/// <c>/session-export</c>): the full conversation history is kept locally in the session's state
/// bag (<c>InMemoryChatHistoryProvider</c>), so the server thread is only an optimization. Clearing
/// the top-level <c>conversationId</c> while leaving the state bag intact drops just the stale
/// pointer; the next turn resends the local history and the service starts a fresh thread — lossless.
/// <para>
/// This type is deliberately host-agnostic (it takes an <see cref="AIAgent"/> and an
/// <see cref="AgentSession"/> and returns a repaired session) so the same logic is reusable across
/// hosts; only the per-host error-handling wiring differs. It lives
/// in this shared <c>MyAssistant.DurableContext</c> library for that reason.
/// </para>
/// <para>
/// DESIGN TRIP-WIRE — server-side use is intentionally RETIRED on the current path, not merely
/// deferred: both wired providers run client-managed history (<c>store=false</c>), which eliminates
/// the stale-pointer (<c>previous_response_not_found</c>) failure class entirely — there is no error
/// for a server-side recovery layer to recover from. It becomes relevant again ONLY if a stateful,
/// server-side-history provider (<c>store=true</c>, holding history behind a <c>previous_response_id</c>
/// / <c>conversationId</c> pointer) is ever wired. A host that holds its session in a plain mutable
/// field can reassign that field to the repaired session on recovery — trivial because that host owns
/// the session. The server (AG-UI host) could NOT do even that: it owns the session and saves it by
/// reference, and the stale pointer is <c>ChatClientAgentSession.ConversationId</c> whose setter is
/// <c>internal</c> (unreachable from this assembly; inheritance can't reach it, and it is not in
/// <c>StateBag</c>), so a repaired session could not be made to persist cross-turn without reflection
/// (rejected). If a stateful provider is ever wired, BUILD the server-side
/// <c>SessionRecoveringAgent : DelegatingAIAgent</c> (override <c>RunCoreStreamingAsync</c>,
/// advance-first) and verify it then — the condition under which it can both fire and be tested.
/// </para>
/// </remarks>
public static class SessionRecovery
{
    /// <summary>
    /// The top-level session-serialization property that holds the server-side response pointer.
    /// Verified against a live exported session.
    /// </summary>
    private const string ConversationIdProperty = "conversationId";

    /// <summary>
    /// Determines whether an exception is the "stale server-side conversation pointer" failure
    /// (HTTP 400 <c>previous_response_not_found</c>), inspecting the whole inner-exception chain so
    /// it still matches when the provider exception is wrapped. Matched by message substring to
    /// avoid a hard dependency on the provider's exception type.
    /// </summary>
    /// <param name="exception">The exception thrown by the streaming run.</param>
    /// <returns><see langword="true"/> if the failure is a missing <c>previous_response_id</c>.</returns>
    public static bool IsStaleConversation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            string message = current.Message;
            if (message.Contains("previous_response_not_found", StringComparison.OrdinalIgnoreCase) is true)
            {
                return true;
            }

            if (message.Contains("previous_response_id", StringComparison.OrdinalIgnoreCase)
                && message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Produces a session equivalent to <paramref name="session"/> but with the stale server-side
    /// conversation pointer cleared, preserving all local state (including the full chat history).
    /// </summary>
    /// <param name="agent">The agent used to serialize and deserialize the session.</param>
    /// <param name="session">The session whose server-side pointer is stale.</param>
    /// <returns>A repaired session, or the original session if its serialized shape was unexpected
    /// and could not be repaired.</returns>
    public static async Task<AgentSession> RepairStaleConversationAsync(AIAgent agent, AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(session);

        JsonElement serialized = await agent.SerializeSessionAsync(session).ConfigureAwait(false);
        if (SessionRecovery.TryStripServerConversationId(serialized, out JsonElement repaired) is false)
        {
            return session;
        }

        return await agent.DeserializeSessionAsync(repaired).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears the top-level <c>conversationId</c> on a serialized session while leaving everything
    /// else (the state bag, and therefore the local chat history) untouched.
    /// </summary>
    /// <param name="serializedSession">The serialized session, as returned by
    /// <see cref="AIAgent.SerializeSessionAsync"/>.</param>
    /// <param name="repaired">The repaired serialized session when the shape was as expected.</param>
    /// <returns><see langword="true"/> if the session was an object and was repaired; otherwise
    /// <see langword="false"/>.</returns>
    internal static bool TryStripServerConversationId(JsonElement serializedSession, out JsonElement repaired)
    {
        if (serializedSession.ValueKind != JsonValueKind.Object)
        {
            repaired = serializedSession;
            return false;
        }

        JsonObject root = JsonNode.Parse(serializedSession.GetRawText())!.AsObject();
        root[SessionRecovery.ConversationIdProperty] = null;
        repaired = JsonSerializer.SerializeToElement(root);
        return true;
    }
}
