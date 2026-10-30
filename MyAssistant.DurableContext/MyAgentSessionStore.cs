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
/// so whatever <c>threadId</c> a client sends resolves to the same snapshot. Registration is via
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
/// Stale-pointer recovery is intentionally <em>not</em> done here: the HTTP 400
/// <c>previous_response_not_found</c> fires when a run executes, not when the session loads, so
/// <see cref="GetSessionAsync"/> has nothing to detect. That recovery is reactive and lives in the
/// run path (see <see cref="SessionRecovery"/> and the forthcoming recovering-agent layer).
/// </para>
/// </remarks>
public sealed class MyAgentSessionStore : AgentSessionStore, IDisposable
{
    private readonly ISnapshotStore _snapshots;

    /// <summary>
    /// Single-writer guard: serializes <see cref="SaveSessionAsync"/> so concurrent saves cannot
    /// interleave their read-modify-write of the one context. Backend-agnostic on purpose — it holds
    /// regardless of whether the snapshot backend is a file or a blob.
    /// </summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>
    /// Initializes the store over a snapshot backend.
    /// </summary>
    /// <param name="snapshots">The swappable persistence backend for the one session snapshot.</param>
    public MyAgentSessionStore(ISnapshotStore snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        this._snapshots = snapshots;
    }

    /// <summary>
    /// Loads the one durable context, or returns <see langword="null"/> if none has been persisted
    /// yet (the framework then creates a fresh session via <c>GetOrCreateSessionAsync</c>). The
    /// incoming <paramref name="key"/> is ignored by design — there is only one context.
    /// </summary>
    public override async ValueTask<AgentSession?> GetSessionAsync(AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);

        string? snapshot = await this._snapshots.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return null;
        }

        JsonElement serialized = JsonSerializer.Deserialize<JsonElement>(snapshot);
        return await agent.DeserializeSessionAsync(serialized, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Persists the one durable context as a snapshot, atomically and under the single-writer guard.
    /// The incoming <paramref name="key"/> is ignored by design — all requests save the one context.
    /// </summary>
    public override async ValueTask SaveSessionAsync(AIAgent agent, AgentSessionStoreKey key, AgentSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(session);

        JsonElement serialized = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken).ConfigureAwait(false);
        string snapshot = serialized.GetRawText();

        await this._writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await this._snapshots.WriteAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this._writeGate.Release();
        }
    }

    /// <summary>
    /// Deletes the one durable context. There is only one context, so the incoming <paramref name="key"/>
    /// is ignored by design (as with <see cref="GetSessionAsync"/> and <see cref="SaveSessionAsync"/>);
    /// a missing snapshot is a no-op, satisfying the contract.
    /// </summary>
    /// <remarks>
    /// We <em>archive</em> rather than hard-delete: <see cref="ISnapshotStore.ArchiveAsync"/> moves the
    /// current snapshot aside under a timestamped name and leaves no current snapshot, so the next
    /// <see cref="GetSessionAsync"/> returns <see langword="null"/> and a fresh context starts. This
    /// matches the project's fresh-context approach (archive for inspectability/recovery, not destruction)
    /// and reuses the same primitive the memory-preserving "new context" command
    /// will call. The archive runs under the <see cref="_writeGate"/> single-writer guard so it cannot
    /// interleave with a concurrent <see cref="SaveSessionAsync"/>.
    /// </remarks>
    /// <param name="agent">The agent that owns this session.</param>
    /// <param name="key">The key that identifies and partitions the session (ignored — one context).</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous delete operation.</returns>
    public override async ValueTask DeleteSessionAsync(AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);

        await this._writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await this._snapshots.ArchiveAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this._writeGate.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        this._writeGate.Dispose();
    }
}
