using System.Globalization;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace MyAssistant.DurableContext;

/// <summary>
/// Azure Blob <see cref="ISnapshotStore"/>: the cloud backend that persists the durable session
/// snapshot as a blob, the drop-in replacement for <see cref="FileSnapshotStore"/> when the app runs
/// where a local filesystem is not the source of truth. The same type serves a real storage account
/// (keyless, via <c>DefaultAzureCredential</c>) and the local Azurite emulator (via its connection
/// string); the caller chooses by how it builds the <see cref="BlobContainerClient"/> and passes it in.
/// </summary>
/// <remarks>
/// <para>
/// The file store's temp-then-atomic-rename does not translate to blobs (there is no rename). The two
/// properties it provided map as follows:
/// </para>
/// <list type="bullet">
///   <item><description><strong>Untearable write</strong> — a single blob upload is atomic at the blob
///   level: a concurrent reader (or a crash) observes either the complete prior blob or the complete new
///   one, never a torn write. So overwriting the current blob in one <c>UploadAsync</c> is the atomic
///   write, no temp blob needed.</description></item>
///   <item><description><strong>One retained prior generation</strong> — before overwriting, the current
///   blob is copied to a <c>.prev</c> blob (read-then-write rather than a server-side copy, so the one
///   code path works identically under both shared-key and Entra auth, and on Azurite whose server-side
///   copy support is partial). Ordering is copy-then-overwrite: a crash between the two leaves the
///   current blob still holding the complete prior snapshot, so no generation is lost.</description></item>
/// </list>
/// <para>
/// Cross-process concurrency: <see cref="ArchiveAsync"/> is guarded with an ETag <c>If-Match</c> so an
/// out-of-process archiver can never delete a snapshot that another writer replaced after the archiver read
/// it (a precondition failure surfaces as <see cref="SnapshotConcurrencyException"/>). Today the archive
/// (the "new conversation" reset) runs in the SERVER process, so the in-process single-writer guard in
/// <see cref="MyAgentSessionStore"/> already serializes it against the per-turn snapshot write; the ETag
/// guard is a backstop that keeps this correct if an out-of-process writer is ever added (e.g. a scheduled
/// job that reads-modifies-writes the context). <see cref="WriteAsync"/> is unguarded across processes for
/// the same reason — the server is its only writer, covered by that same in-process guard. See
/// SERVER-SECURITY-NOTES.md.
/// </para>
/// </remarks>
public sealed class BlobSnapshotStore : ISnapshotStore
{
    private const string CurrentBlobName = "session.json";
    private const string PreviousBlobName = "session.prev.json";

    private BlobContainerClient Container { get; }

    /// <summary>
    /// Initializes the store over a container client the caller has already pointed at the target account
    /// (real account + <c>DefaultAzureCredential</c>, or an Azurite connection string). The container is
    /// created on first write if it does not exist.
    /// </summary>
    /// <param name="container">The container that holds the snapshot blobs.</param>
    public BlobSnapshotStore(BlobContainerClient container)
    {
        ArgumentNullException.ThrowIfNull(container);
        this.Container = container;
    }

    /// <inheritdoc/>
    public async ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Response<BlobDownloadResult> current =
                await this.Container.GetBlobClient(CurrentBlobName)
                    .DownloadContentAsync(cancellationToken).ConfigureAwait(false);
            return current.Value.Content.ToString();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // 404 covers both a missing blob and a missing container: no durable context yet.
            return null;
        }
    }

    /// <inheritdoc/>
    public async ValueTask WriteAsync(string snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        await this.Container.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        BlobClient currentBlob = this.Container.GetBlobClient(CurrentBlobName);

        // Retain one prior generation: copy current -> .prev before overwriting. Skipped on the first
        // write, when there is no current blob to back up.
        try
        {
            Response<BlobDownloadResult> existing =
                await currentBlob.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
            await this.Container.GetBlobClient(PreviousBlobName)
                .UploadAsync(existing.Value.Content, overwrite: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // No current snapshot yet — nothing to retain.
        }

        // Atomic overwrite of the current snapshot.
        await currentBlob
            .UploadAsync(BinaryData.FromString(snapshot), overwrite: true, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <exception cref="SnapshotConcurrencyException">The current snapshot was replaced by another
    /// writer between the read and the conditional delete (ETag <c>If-Match</c> precondition failed);
    /// the newer snapshot is left current and the archive did not complete.</exception>
    public async ValueTask ArchiveAsync(CancellationToken cancellationToken = default)
    {
        // Nothing to archive if the container was never created (no snapshot has ever been written).
        if ((await this.Container.ExistsAsync(cancellationToken).ConfigureAwait(false)).Value is false)
        {
            return;
        }

        BlobClient currentBlob = this.Container.GetBlobClient(CurrentBlobName);

        // Read the current snapshot and capture its ETag — the identity we condition the delete on below.
        BinaryData currentContent;
        ETag currentETag;
        try
        {
            Response<BlobDownloadResult> current =
                await currentBlob.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
            currentContent = current.Value.Content;
            currentETag = current.Value.Details.ETag;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // No current snapshot — archiving is a no-op (matches the file store).
            return;
        }

        // Copy the snapshot we just read to a timestamped archive BEFORE deleting the current blob, so a
        // crash between the two leaves both the archive and the (still-complete) current blob — no
        // generation is lost.
        string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        await this.Container.GetBlobClient($"session.{stamp}.json")
            .UploadAsync(currentContent, overwrite: true, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Delete the current snapshot, but ONLY if it is still the generation we archived (If-Match on the
        // captured ETag). The agent server rewrites session.json every turn in a SEPARATE process; if a
        // turn landed between the read above and this delete, the ETag no longer matches and the service
        // returns 412. Deleting then would discard a snapshot written AFTER the user's reset decision, so
        // abort instead and let the caller retry. The timestamped archive of the prior generation stays
        // (a harmless extra copy — no data is lost; the newer snapshot remains current).
        try
        {
            await currentBlob
                .DeleteIfExistsAsync(
                    DeleteSnapshotsOption.None,
                    new BlobRequestConditions { IfMatch = currentETag },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 412)
        {
            throw new SnapshotConcurrencyException(
                "The session snapshot changed during archive (a turn was written concurrently); the reset did not complete.",
                ex);
        }

        // Drop the one-generation .prev backup too: it is crash insurance for the CURRENT snapshot's
        // write, and once the current snapshot is archived there is no current write to protect. The
        // timestamped archive already captures the conversation's final state. DeleteIfExists is a no-op
        // when the blob is absent.
        await this.Container.GetBlobClient(PreviousBlobName)
            .DeleteIfExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
