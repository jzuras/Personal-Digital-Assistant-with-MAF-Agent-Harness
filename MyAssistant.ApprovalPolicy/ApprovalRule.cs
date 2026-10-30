using System.Text.Json.Serialization;

namespace MyAssistant.ApprovalPolicy;

// DESIGN TRIP-WIRE — read before changing what a durable grant is bound to.
// A grant currently binds to tool name + exact canonicalized args only (no target content).
// If we ever add a tool whose args do NOT fully describe the target it mutates/destroys
// (e.g. replace_note_content, delete_note by id), a standing "yes" can silently act on a
// target that drifted since approval (TOCTOU). The deferred guard is content-snapshot
// hashing — considered and deferred as overkill for current/near-term tools. Reconsider it
// before extending this record.

/// <summary>
/// A single durable approval grant: a tool invoked with one exact, canonicalized set of
/// arguments is auto-approved in all future sessions. Persisted as one entry in
/// <c>approval-rules.json</c>. Only exact-argument grants exist by design — there is no
/// "any arguments" rule.
/// </summary>
public sealed record ApprovalRule
{
    /// <summary>
    /// Gets the registered tool name this grant applies to (e.g. <c>get_stock_price</c>).
    /// Matches <c>FunctionCallContent.Name</c> seen by the auto-approval predicate.
    /// </summary>
    [JsonPropertyName("tool")]
    public required string ToolName { get; init; }

    /// <summary>
    /// Gets the canonicalized argument string the call must match exactly to be
    /// auto-approved. Produced by <see cref="ArgCanonicalizer.Canonicalize"/> so comparison
    /// is stable across runs.
    /// </summary>
    [JsonPropertyName("args")]
    public required string CanonicalArguments { get; init; }

    /// <summary>
    /// Gets the UTC timestamp at which the user granted this durable approval.
    /// </summary>
    [JsonPropertyName("grantedUtc")]
    public required DateTimeOffset GrantedUtc { get; init; }
}
