namespace MyAssistant.DurableContext;

/// <summary>
/// Thrown by <see cref="ISnapshotStore.ArchiveAsync"/> when the current snapshot changed under the
/// archiver — a different writer replaced it between the read that captured its identity and the
/// conditional delete. The archive did not complete and left the newer snapshot current; the caller
/// should surface a retryable conflict rather than report a successful reset.
/// </summary>
/// <remarks>
/// Backs a cross-process reset race: if an out-of-process archiver (e.g. a future scheduled job) ever
/// archives the context while the server rewrites the session every turn, <see cref="BlobSnapshotStore"/>
/// guards the delete with an ETag <c>If-Match</c> and a precondition failure surfaces here. In the normal
/// path the reset archives in the server process, serialized against the per-turn write by the in-process
/// single-writer guard. The file backend handles this race by UI coordination instead, so it never raises
/// this.
/// </remarks>
public sealed class SnapshotConcurrencyException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SnapshotConcurrencyException"/> class.
    /// </summary>
    public SnapshotConcurrencyException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SnapshotConcurrencyException"/> class with a
    /// message describing the conflict.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public SnapshotConcurrencyException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SnapshotConcurrencyException"/> class with a
    /// message and the underlying store exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The underlying exception (e.g. the service precondition failure).</param>
    public SnapshotConcurrencyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
