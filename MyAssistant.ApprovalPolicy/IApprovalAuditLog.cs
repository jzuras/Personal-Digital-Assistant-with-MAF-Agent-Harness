namespace MyAssistant.ApprovalPolicy;

/// <summary>
/// Records every silent auto-approval so that even granted actions are visible after the
/// fact (Muse's audit-trail principle). Consulted by <see cref="ApprovalRuleStore"/> whenever
/// a durable rule fires.
/// </summary>
public interface IApprovalAuditLog
{
    /// <summary>
    /// Records that a durable rule silently auto-approved a tool call.
    /// </summary>
    /// <param name="toolName">The registered tool name that was auto-approved.</param>
    /// <param name="canonicalArguments">The canonical arguments that matched a durable rule.</param>
    /// <param name="whenUtc">The UTC time the auto-approval fired.</param>
    /// <returns>A task that completes when the entry has been recorded.</returns>
    Task RecordAutoApprovalAsync(string toolName, string canonicalArguments, DateTimeOffset whenUtc);
}
