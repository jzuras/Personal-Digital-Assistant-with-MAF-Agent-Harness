using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Agents.AI;

namespace MyAssistant.DurableContext;

/// <summary>Azure Blob implementation of the agent file store (flat blobs; "/" emulates directories).</summary>
public sealed class BlobAgentFileStore : AgentFileStore
{
    private const string FileType = "file";
    private const string DirectoryType = "directory";

    private BlobContainerClient Container { get; }

    /// <summary>Initializes the store over a container client the caller has pointed at the target account.</summary>
    public BlobAgentFileStore(BlobContainerClient container)
    {
        ArgumentNullException.ThrowIfNull(container);
        this.Container = container;
    }

    private static string NormalizeFile(string path)
    {
        return path.Replace('\\', '/').Trim('/');
    }

    private static string NormalizeDirPrefix(string directory)
    {
        string d = directory.Replace('\\', '/').Trim('/');
        return d.Length == 0 ? string.Empty : d + "/";
    }

    /// <inheritdoc/>
    public override async Task WriteAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(content);

        await this.Container.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await this.Container.GetBlobClient(NormalizeFile(path))
            .UploadAsync(BinaryData.FromString(content), overwrite: true, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        try
        {
            Response<BlobDownloadResult> r = await this.Container.GetBlobClient(NormalizeFile(path))
                .DownloadContentAsync(cancellationToken).ConfigureAwait(false);
            return r.Value.Content.ToString();
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public override async Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        Response<bool> deleted = await this.Container.GetBlobClient(NormalizeFile(path))
            .DeleteIfExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return deleted.Value;
    }

    /// <inheritdoc/>
    public override async Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        Response<bool> exists = await this.Container.GetBlobClient(NormalizeFile(path))
            .ExistsAsync(cancellationToken).ConfigureAwait(false);
        return exists.Value;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A no-op: Azure Blob storage has no empty directories. A directory becomes observable the moment a
    /// file is written beneath it (surfaced as a hierarchy prefix by <see cref="ListChildrenAsync"/>), so
    /// there is nothing to create up front. Returning success lets a subsequent write under the path
    /// proceed exactly as the local file store allows.
    /// </remarks>
    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        // DESIGN TRIP-WIRE — the ONE behavioral divergence from the local file store. Nested files and
        // directories work fine here (a blob named "a/b/c.txt" lists as a/ -> b/ -> c.txt via delimiter
        // prefixes, at any depth). What a blob store cannot represent is an EMPTY directory: a directory
        // exists only as a prefix derived from a real blob name, so one with no files under it is invisible
        // and this call cannot materialize it. Today the store is used flat (root-level files only), so this
        // never bites. If a future feature adds sub-directories: creating-then-listing an empty directory
        // will succeed on the local file store but show nothing here — do not rely on it, and test any
        // directory-sensitive behavior against BOTH backends.
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directory);

        string prefix = NormalizeDirPrefix(directory);
        List<FileStoreEntry> entries = [];

        if ((await this.Container.ExistsAsync(cancellationToken).ConfigureAwait(false)).Value is false)
        {
            return entries;
        }

        await foreach (BlobHierarchyItem item in this.Container
            .GetBlobsByHierarchyAsync(default, default, "/", prefix, cancellationToken).ConfigureAwait(false))
        {
            if (item.IsPrefix is true)
            {
                string leaf = item.Prefix[prefix.Length..].TrimEnd('/');
                if (leaf.Length > 0)
                {
                    entries.Add(new FileStoreEntry(leaf, DirectoryType));
                }
            }
            else
            {
                string leaf = item.Blob.Name[prefix.Length..];
                if (leaf.Length > 0 && leaf.EndsWith('/') is false)
                {
                    entries.Add(new FileStoreEntry(leaf, FileType));
                }
            }
        }

        return entries;
    }
}
