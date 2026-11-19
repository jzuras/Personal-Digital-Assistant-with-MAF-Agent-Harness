namespace MyAssistant.ApprovalPolicy;

/// <summary>
/// Local-filesystem <see cref="IPolicyTextStore"/>: the dev backend that keeps the policy
/// artifacts as plain files under a dedicated directory (kept separate from the agent's file-access
/// store, so the agent cannot read or alter its own approval rules and audit trail). The cloud
/// end-state swaps in a blob-backed implementation of the same interface.
/// </summary>
public sealed class FilePolicyTextStore : IPolicyTextStore
{
    private string RootDirectory { get; }

    /// <summary>
    /// Serializes concurrent appends so audit/log lines are never interleaved.
    /// </summary>
    private SemaphoreSlim AppendGate { get; } = new(1, 1);

    /// <summary>
    /// Initializes the store rooted at <paramref name="rootDirectory"/> (created on first write).
    /// </summary>
    /// <param name="rootDirectory">The directory that holds the policy documents (e.g. an app-relative
    /// <c>policy/</c> folder).</param>
    public FilePolicyTextStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        this.RootDirectory = rootDirectory;
    }

    private string PathFor(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Path.Combine(this.RootDirectory, name);
    }

    /// <inheritdoc/>
    public async ValueTask<string?> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        string path = this.PathFor(name);
        if (File.Exists(path) is false)
        {
            return null;
        }

        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask WriteAsync(string name, string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        Directory.CreateDirectory(this.RootDirectory);
        await File.WriteAllTextAsync(this.PathFor(name), content, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask AppendLineAsync(string name, string line, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(line);

        await this.AppendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(this.RootDirectory);
            await File.AppendAllTextAsync(this.PathFor(name), line + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.AppendGate.Release();
        }
    }

    /// <inheritdoc/>
    public string DescribeLocation(string name)
    {
        return this.PathFor(name);
    }
}
