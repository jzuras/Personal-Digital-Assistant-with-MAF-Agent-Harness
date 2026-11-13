namespace MyAssistant.ApprovalPolicy;

/// <summary>
/// Minimal, framework-agnostic persistence seam for the policy / accountability artifacts — the
/// approval-rule document and the append-only audit / send logs. Kept dependency-free (in-box
/// <c>System.Text.Json</c> only) so this security-critical library never couples to a storage SDK;
/// the host supplies a file-backed implementation for local dev and a blob-backed one for the cloud.
/// A given document name is used with EITHER read/write (the rule document) OR append-line (a log),
/// never both.
/// </summary>
public interface IPolicyTextStore
{
    /// <summary>
    /// Reads the whole named document, or returns <see langword="null"/> when it does not exist yet.
    /// </summary>
    /// <param name="name">The document name within the store.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    ValueTask<string?> ReadAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes (overwriting) the whole named document.
    /// </summary>
    /// <param name="name">The document name within the store.</param>
    /// <param name="content">The full content to persist.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    ValueTask WriteAsync(string name, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends one line to the named append-only log; the store adds the trailing newline.
    /// </summary>
    /// <param name="name">The log document name within the store.</param>
    /// <param name="line">The line to append (without a trailing newline).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    ValueTask AppendLineAsync(string name, string line, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a human-readable description of where the named document lives, surfaced so the UI can
    /// tell the user where a grant is recorded (and therefore how to revoke it).
    /// </summary>
    /// <param name="name">The document name within the store.</param>
    string DescribeLocation(string name);
}
