namespace MyAssistant.ApprovalPolicy;

/// <summary>
/// An <see cref="IApprovalAuditLog"/> that appends one human-readable line per silent
/// auto-approval to a log file. The file is a durable, inspectable record alongside
/// <c>approval-rules.json</c> in the working directory.
/// </summary>
public sealed class FileApprovalAuditLog : IApprovalAuditLog
{
    /// <summary>
    /// Gets the full path of the audit log file.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Serializes concurrent appends so lines are never interleaved.
    /// </summary>
    private SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="FileApprovalAuditLog"/> class.
    /// </summary>
    /// <param name="filePath">The full path of the audit log file to append to.</param>
    public FileApprovalAuditLog(string filePath)
    {
        this.FilePath = filePath;
    }

    /// <inheritdoc/>
    public async Task RecordAutoApprovalAsync(string toolName, string canonicalArguments, DateTimeOffset whenUtc)
    {
        string line = $"{whenUtc:o}\tAUTO-APPROVE\t{toolName}\t{canonicalArguments}{Environment.NewLine}";

        await this.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            string? directory = Path.GetDirectoryName(this.FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.AppendAllTextAsync(this.FilePath, line).ConfigureAwait(false);
        }
        finally
        {
            this.Gate.Release();
        }
    }
}
