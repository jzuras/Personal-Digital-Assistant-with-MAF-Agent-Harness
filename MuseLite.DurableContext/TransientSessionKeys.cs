namespace MyAssistant.DurableContext;

/// <summary>
/// Reserved session-key shapes that mark a conversation as transient — i.e. NOT the one durable
/// user context. The server's request-adaptation layer assigns these thread ids to runs that
/// originate from the hosting platform's own invokers (scheduled routines and portal playground
/// test chats) rather than from the user's real client, and <see cref="MyAgentSessionStore"/>
/// branches on them so such runs can never read or write the durable context snapshot.
/// </summary>
/// <remarks>
/// Both the adapter (which stamps the thread id) and the session store (which branches on it) must
/// agree on these values, which is why they live here in the shared library rather than in either
/// consumer. Ordinal comparison everywhere: these are machine-chosen constants, never user input.
/// </remarks>
public static class TransientSessionKeys
{
    /// <summary>
    /// The thread id assigned to unattended scheduled-routine runs. Routine runs are fully
    /// ephemeral: no history is loaded and nothing is persisted — a scheduled task run's durable
    /// state belongs in the task files it maintains, not in chat history.
    /// </summary>
    public const string RoutineSessionId = "foundry-routine";

    /// <summary>
    /// The thread-id prefix for portal playground test chats. The platform session id of the
    /// playground conversation is appended, so each playground gets its own context: held
    /// in memory only (multi-turn continuity while the host stays warm), never persisted.
    /// </summary>
    public const string PlaygroundSessionIdPrefix = "playground:";

    /// <summary>
    /// Determines whether a session id is the reserved unattended-routine id.
    /// </summary>
    /// <param name="sessionId">The session id from the store key, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> for the routine id; otherwise <see langword="false"/>.</returns>
    public static bool IsRoutine(string? sessionId)
    {
        return string.Equals(sessionId, TransientSessionKeys.RoutineSessionId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Determines whether a session id denotes a playground conversation context.
    /// </summary>
    /// <param name="sessionId">The session id from the store key, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> for a playground-prefixed id; otherwise <see langword="false"/>.</returns>
    public static bool IsPlayground(string? sessionId)
    {
        return sessionId is not null
            && sessionId.StartsWith(TransientSessionKeys.PlaygroundSessionIdPrefix, StringComparison.Ordinal);
    }
}
