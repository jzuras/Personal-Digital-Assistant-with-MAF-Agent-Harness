namespace MyAssistant.ApprovalPolicy;

/// <summary>
/// An <see cref="IApprovalAuditLog"/> that appends one human-readable line per silent auto-approval
/// to an append-only log in an <see cref="IPolicyTextStore"/> — a durable, inspectable record
/// alongside the approval-rule document, in the same (agent-inaccessible) policy store.
/// </summary>
public sealed class ApprovalAuditLog : IApprovalAuditLog
{
    private IPolicyTextStore Store { get; }

    private string Name { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ApprovalAuditLog"/> class.
    /// </summary>
    /// <param name="store">The backing policy store.</param>
    /// <param name="name">The log document name within the store (e.g. <c>approval-audit.log</c>).</param>
    public ApprovalAuditLog(IPolicyTextStore store, string name)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        this.Store = store;
        this.Name = name;
    }

    /// <inheritdoc/>
    public async Task RecordAutoApprovalAsync(string toolName, string canonicalArguments, DateTimeOffset whenUtc)
    {
        string line = $"{whenUtc:o}\tAUTO-APPROVE\t{toolName}\t{canonicalArguments}";
        await this.Store.AppendLineAsync(this.Name, line).ConfigureAwait(false);
    }
}
