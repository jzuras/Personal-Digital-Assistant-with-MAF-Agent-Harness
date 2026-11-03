namespace MyAssistant.DurableContext;

/// <summary>
/// Backend-swappable persistence for a single opaque session snapshot blob. This is the one seam
/// that differs per hosting environment: a filesystem implementation in local dev/test
/// (<see cref="FileSnapshotStore"/>), a blob-backed implementation (S3/Azure Blob) in the cloud
/// end-state. <see cref="MyAgentSessionStore"/> depends only on this interface, so moving to the
/// cloud swaps the implementation and nothing else.
/// </summary>
/// <remarks>
/// The contract is intentionally tiny — read / write-atomically / archive — because that is exactly
/// what varies between a local file and a cloud blob. Crash-safety (an untearable write) is promised
/// by <see cref="WriteAsync"/>; concurrency across callers is the caller's concern (the single-writer
/// guard lives in <see cref="MyAgentSessionStore"/>).
/// </remarks>
public interface ISnapshotStore
{
    /// <summary>
    /// Reads the current snapshot, or returns <see langword="null"/> if none has been written yet
    /// (the signal <see cref="MyAgentSessionStore.GetSessionAsync"/> maps to "no durable context yet").
    /// </summary>
    ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes <paramref name="snapshot"/> as the new current snapshot <em>atomically</em>: a reader
    /// (or a crash) must observe either the complete prior snapshot or the complete new one, never a
    /// torn write. Implementations also retain one prior generation as cheap crash/inspection insurance.
    /// </summary>
    /// <param name="snapshot">The serialized session JSON to persist.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    ValueTask WriteAsync(string snapshot, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the current snapshot aside under a timestamped name and leaves no current snapshot, so a
    /// subsequent <see cref="ReadAsync"/> returns <see langword="null"/> and the next turn starts an
    /// empty context. Backs the memory-preserving "new context" command; a no-op when there is no
    /// current snapshot.
    /// </summary>
    /// <remarks>
    /// This primitive backs the memory-preserving "new conversation" reset: the server's
    /// <c>POST /my_assistant/reset</c> endpoint calls it through
    /// <see cref="MyAgentSessionStore.DeleteSessionAsync"/> (the framework's session-delete contract
    /// archives via this method) to retire the current context and start an empty one, while the
    /// user-editable memory files are a separate store left untouched. Archiving rather than
    /// hard-deleting keeps the prior conversation available for inspection/recovery.
    /// </remarks>
    ValueTask ArchiveAsync(CancellationToken cancellationToken = default);
}
