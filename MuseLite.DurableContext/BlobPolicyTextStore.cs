using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using MyAssistant.ApprovalPolicy;

namespace MyAssistant.DurableContext;

/// <summary>Azure Blob implementation of the policy store: a block blob for the rule document,
/// append blobs for the append-only logs. Lives in an agent-inaccessible container.</summary>
public sealed class BlobPolicyTextStore : IPolicyTextStore
{
    private BlobContainerClient Container { get; }

    /// <summary>Initializes the store over a container client the caller has pointed at the target account.</summary>
    /// <param name="container">The container that holds the policy documents.</param>
    public BlobPolicyTextStore(BlobContainerClient container)
    {
        ArgumentNullException.ThrowIfNull(container);
        this.Container = container;
    }

    /// <inheritdoc/>
    public async ValueTask<string?> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        try
        {
            Response<BlobDownloadResult> r = await this.Container.GetBlobClient(name)
                .DownloadContentAsync(cancellationToken).ConfigureAwait(false);
            return r.Value.Content.ToString();
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async ValueTask WriteAsync(string name, string content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(content);
        await this.Container.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await this.Container.GetBlobClient(name)
            .UploadAsync(BinaryData.FromString(content), overwrite: true, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask AppendLineAsync(string name, string line, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(line);
        await this.Container.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        AppendBlobClient appendBlob = this.Container.GetAppendBlobClient(name);
        await appendBlob.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        using var stream = new MemoryStream(bytes);
        await appendBlob.AppendBlockAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public string DescribeLocation(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return $"{this.Container.Uri}/{name}";
    }
}
