using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Agents.AI;

namespace MyAssistant.DurableContext;

/// <summary>
/// The custom <see cref="AgentSessionStore"/> that makes "one durable context" real on the server:
/// it routes <em>every</em> incoming request to a single fixed context, ignoring the client's
/// per-request session id, and persists that one context as a serialized session snapshot that
/// survives restarts.
/// </summary>
/// <remarks>
/// <para>
/// Routing is trivial: <see cref="AgentSessionStoreKey.SessionId"/> is deliberately ignored,
/// so whatever <c>threadId</c> a client sends resolves to the same snapshot — with one carve-out:
/// the reserved <see cref="TransientSessionKeys"/> ids mark runs from the hosting platform's own
/// invokers (scheduled routines, portal playground chats), which must never touch the user's
/// durable context. Routine keys get an ephemeral, no-persist context; playground keys get a
/// bounded in-memory context per playground conversation. Registration is via
/// <c>WithSessionStore(store, withIsolation:false)</c> on the server (the <c>withIsolation</c> flag is
/// the multi-user knob, left off for single-user — see SERVER-SECURITY-NOTES.md).
/// </para>
/// <para>
/// Persistence is delegated to a swappable <see cref="ISnapshotStore"/> (filesystem now, blob later).
/// The MAF session-store contract is a plain save/get of an opaque session with <em>no</em>
/// compare-and-swap or "consume" primitive (reflection-confirmed, 2026-10-24), so the two durability
/// properties are ours to provide: <strong>crash-safe atomic writes</strong> live in the snapshot
/// store (temp-then-atomic-rename), and a <strong>single-writer guard</strong> lives here, serializing
/// saves so a resume cannot interleave and double-apply a pending approval.
/// </para>
/// <para>
/// Stale-pointer recovery is intentionally <em>not</em> done here. That failure class (HTTP 400
/// <c>previous_response_not_found</c>) only arises with service-managed history (<c>store=true</c>),
/// where each run chains to a prior server-held response that can expire; this server runs
/// client-managed history (<c>store=false</c>), which carries the full transcript inside the snapshot
/// and therefore cannot hit it. The host-agnostic <see cref="SessionRecovery"/> helper remains for a
/// service-managed host, but no recovering-agent layer is composed here — see the
/// trip-wire comment in the server's agent-composition code for when to revisit.
/// </para>
/// </remarks>
public sealed class MyAgentSessionStore : AgentSessionStore, IDisposable
{
    private ISnapshotStore Snapshots { get; }

    /// <summary>
    /// Single-writer guard: serializes <see cref="SaveSessionAsync"/> so concurrent saves cannot
    /// interleave their read-modify-write of the one context. Backend-agnostic on purpose — it holds
    /// regardless of whether the snapshot backend is a file or a blob.
    /// </summary>
    private SemaphoreSlim WriteGate { get; } = new(1, 1);

    /// <summary>
    /// In-memory contexts for playground conversations, keyed by their full session id. Memory-only
    /// by design: playground chats are disposable test conversations, so losing them on restart or
    /// scale-to-zero recycle is acceptable, and persisting them would accrete storage litter nothing
    /// ever cleans up. Bounded by <see cref="MaxPlaygroundContexts"/> with least-recently-used
    /// eviction so an unbounded stream of playground sessions cannot grow memory.
    /// </summary>
    private ConcurrentDictionary<string, PlaygroundContext> PlaygroundContexts { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Upper bound on concurrently retained playground contexts. Generous for a single-user
    /// deployment (one person rarely has more than a couple of playground chats open at once).
    /// </summary>
    private const int MaxPlaygroundContexts = 16;

    /// <summary>
    /// Initializes the store over a snapshot backend.
    /// </summary>
    /// <param name="snapshots">The swappable persistence backend for the one session snapshot.</param>
    public MyAgentSessionStore(ISnapshotStore snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        this.Snapshots = snapshots;
    }

    /// <summary>
    /// Loads the one durable context, or returns <see langword="null"/> if none has been persisted
    /// yet (the framework then creates a fresh session via <c>GetOrCreateSessionAsync</c>). The
    /// incoming <paramref name="key"/> is ignored by design — there is only one context — except for
    /// the reserved <see cref="TransientSessionKeys"/> ids: a routine run always starts fresh
    /// (<see langword="null"/>), and a playground run resumes its own in-memory context when one is
    /// retained. Neither ever reads the durable snapshot.
    /// </summary>
    public override async ValueTask<AgentSession?> GetSessionAsync(AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(key);

        if (TransientSessionKeys.IsRoutine(key.SessionId) is true)
        {
            return null;
        }

        if (key.SessionId is string playgroundId && TransientSessionKeys.IsPlayground(playgroundId) is true)
        {
            if (this.PlaygroundContexts.TryGetValue(playgroundId, out PlaygroundContext? retained) is false)
            {
                return null;
            }

            retained.LastTouched = Environment.TickCount64;
            JsonElement retainedSerialized = JsonSerializer.Deserialize<JsonElement>(retained.Snapshot);
            return await agent.DeserializeSessionAsync(retainedSerialized, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        string? snapshot = await this.Snapshots.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return null;
        }

        JsonElement serialized = JsonSerializer.Deserialize<JsonElement>(snapshot);
        return await agent.DeserializeSessionAsync(serialized, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Persists the one durable context as a snapshot, atomically and under the single-writer guard.
    /// The incoming <paramref name="key"/> is ignored by design — all requests save the one context —
    /// except for the reserved <see cref="TransientSessionKeys"/> ids: a routine save is a no-op
    /// (fully ephemeral), and a playground save goes to the bounded in-memory map. Neither ever
    /// writes the durable snapshot, so platform-originated runs provably cannot alter the user's
    /// context.
    /// </summary>
    public override async ValueTask SaveSessionAsync(AIAgent agent, AgentSessionStoreKey key, AgentSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(session);

        if (TransientSessionKeys.IsRoutine(key.SessionId) is true)
        {
            return;
        }

        if (key.SessionId is string playgroundId && TransientSessionKeys.IsPlayground(playgroundId) is true)
        {
            JsonElement playgroundSerialized = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken).ConfigureAwait(false);
            this.EvictPlaygroundContextIfFull(playgroundId);
            this.PlaygroundContexts[playgroundId] = new PlaygroundContext
            {
                Snapshot = playgroundSerialized.GetRawText(),
                LastTouched = Environment.TickCount64,
            };
            return;
        }

        JsonElement serialized = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken).ConfigureAwait(false);
        string snapshot = serialized.GetRawText();

        await this.WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await this.Snapshots.WriteAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.WriteGate.Release();
        }
    }

    /// <summary>
    /// Deletes the one durable context. There is only one context, so the incoming <paramref name="key"/>
    /// is ignored by design (as with <see cref="GetSessionAsync"/> and <see cref="SaveSessionAsync"/>),
    /// except for the reserved <see cref="TransientSessionKeys"/> ids, which only ever affect their
    /// own transient state; a missing snapshot is a no-op, satisfying the contract.
    /// </summary>
    /// <remarks>
    /// We <em>archive</em> rather than hard-delete: <see cref="ISnapshotStore.ArchiveAsync"/> moves the
    /// current snapshot aside under a timestamped name and leaves no current snapshot, so the next
    /// <see cref="GetSessionAsync"/> returns <see langword="null"/> and a fresh context starts. This
    /// matches the project's fresh-context approach (archive for inspectability/recovery, not destruction)
    /// and reuses the same primitive the memory-preserving "new context" command
    /// will call. The archive runs under the <see cref="WriteGate"/> single-writer guard so it cannot
    /// interleave with a concurrent <see cref="SaveSessionAsync"/>.
    /// </remarks>
    /// <param name="agent">The agent that owns this session.</param>
    /// <param name="key">The key that identifies and partitions the session (ignored — one context).</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous delete operation.</returns>
    public override async ValueTask DeleteSessionAsync(AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(key);

        // Transient keys never touch the durable snapshot: deleting a routine context is meaningless
        // (nothing was kept), and deleting a playground context just drops its in-memory entry. The
        // archive below must only ever run for the one durable user context.
        if (TransientSessionKeys.IsRoutine(key.SessionId) is true)
        {
            return;
        }

        if (key.SessionId is string playgroundId && TransientSessionKeys.IsPlayground(playgroundId) is true)
        {
            this.PlaygroundContexts.TryRemove(playgroundId, out _);
            return;
        }

        await this.WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await this.Snapshots.ArchiveAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.WriteGate.Release();
        }
    }

    /// <summary>
    /// Makes room for a new playground context when the retention bound is reached, evicting the
    /// least-recently-touched entry. Updating an already-retained id never evicts. Scans linearly —
    /// fine at the configured bound; revisit if <see cref="MaxPlaygroundContexts"/> ever grows large.
    /// </summary>
    /// <param name="incomingSessionId">The playground session id about to be saved.</param>
    private void EvictPlaygroundContextIfFull(string incomingSessionId)
    {
        if (this.PlaygroundContexts.ContainsKey(incomingSessionId) is true
            || this.PlaygroundContexts.Count < MyAgentSessionStore.MaxPlaygroundContexts)
        {
            return;
        }

        string? oldestId = null;
        long oldestTouch = long.MaxValue;
        foreach (KeyValuePair<string, PlaygroundContext> pair in this.PlaygroundContexts)
        {
            if (pair.Value.LastTouched < oldestTouch)
            {
                oldestTouch = pair.Value.LastTouched;
                oldestId = pair.Key;
            }
        }

        if (oldestId is not null)
        {
            this.PlaygroundContexts.TryRemove(oldestId, out _);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        this.WriteGate.Dispose();
    }

    /// <summary>
    /// A retained in-memory playground conversation: its serialized session snapshot plus a
    /// last-touched stamp (from <see cref="Environment.TickCount64"/>) for least-recently-used
    /// eviction.
    /// </summary>
    private sealed class PlaygroundContext
    {
        /// <summary>The serialized session snapshot, exactly as the agent serialized it.</summary>
        public required string Snapshot { get; init; }

        /// <summary>When this context was last read or written, for eviction ordering.</summary>
        public long LastTouched { get; set; }
    }
}
