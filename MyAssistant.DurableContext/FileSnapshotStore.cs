// Copyright (c) Microsoft. All rights reserved.

namespace MyAssistant.DurableContext;

/// <summary>
/// Local-filesystem <see cref="ISnapshotStore"/>: the dev/test backend that writes the durable
/// session snapshot as readable JSON into a <c>context/</c> directory, machine-owned and kept out of
/// the user-editable <c>working/</c> store. The cloud end-state replaces this with a blob-backed
/// implementation of the same interface.
/// </summary>
/// <remarks>
/// Atomicity is provided by write-temp-then-atomic-rename: the new snapshot is written to a
/// <c>.tmp</c> sibling, then promoted with <see cref="File.Replace(string, string, string?)"/>, which
/// on NTFS atomically swaps the current file in and moves the old one to the <c>.prev.json</c> backup
/// — so a crash mid-write leaves the prior snapshot intact, and one prior generation is retained for
/// free. The first-ever write (no current file to replace) falls back to an atomic
/// <see cref="File.Move(string, string)"/>.
/// </remarks>
public sealed class FileSnapshotStore : ISnapshotStore
{
    private const string CurrentFileName = "session.json";
    private const string PreviousFileName = "session.prev.json";
    private const string TempFileName = "session.json.tmp";

    private string DirectoryPath { get; }
    private string CurrentPath { get; }
    private string PreviousPath { get; }
    private string TempPath { get; }

    /// <summary>
    /// Initializes the store rooted at <paramref name="directoryPath"/> (created on first write).
    /// </summary>
    /// <param name="directoryPath">The directory that holds the snapshot files (e.g. an app-relative
    /// <c>context/</c> folder). The hosting app chooses the location, mirroring how the
    /// <c>working/</c> file store is rooted.</param>
    public FileSnapshotStore(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        this.DirectoryPath = directoryPath;
        this.CurrentPath = Path.Combine(directoryPath, CurrentFileName);
        this.PreviousPath = Path.Combine(directoryPath, PreviousFileName);
        this.TempPath = Path.Combine(directoryPath, TempFileName);
    }

    /// <inheritdoc/>
    public async ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(this.CurrentPath) is false)
        {
            return null;
        }

        return await File.ReadAllTextAsync(this.CurrentPath, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask WriteAsync(string snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Directory.CreateDirectory(this.DirectoryPath);
        await File.WriteAllTextAsync(this.TempPath, snapshot, cancellationToken).ConfigureAwait(false);

        if (File.Exists(this.CurrentPath) is true)
        {
            // Atomic swap: temp becomes current, the old current becomes the .prev backup.
            File.Replace(this.TempPath, this.CurrentPath, this.PreviousPath);
        }
        else
        {
            // First write: no current file to replace, so an atomic rename is enough.
            File.Move(this.TempPath, this.CurrentPath);
        }
    }

    /// <inheritdoc/>
    public ValueTask ArchiveAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(this.CurrentPath) is true)
        {
            string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
            string archivePath = Path.Combine(this.DirectoryPath, $"session.{stamp}.json");
            File.Move(this.CurrentPath, archivePath);
        }

        // Drop the one-generation .prev backup too. It is crash insurance for the CURRENT snapshot's
        // write; once the current snapshot is archived there is no current write to protect, and the
        // timestamped archive already captures the conversation's final state. Leaving .prev behind would
        // strand a stale fragment of the just-archived conversation outside the archive (it is not touched
        // by the next conversation's first write, which takes the no-current File.Move path). Removing it
        // keeps a reset clean: context/ holds only the timestamped archive(s), and the next conversation
        // rebuilds its own .prev naturally.
        File.Delete(this.PreviousPath);

        return ValueTask.CompletedTask;
    }
}
