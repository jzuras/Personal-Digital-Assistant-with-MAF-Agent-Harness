using System.Text.Json.Serialization;

namespace MyAssistant.ApprovalPolicy;

/// <summary>
/// A single durable approval grant: a tool invoked with one exact, canonicalized set of
/// arguments is auto-approved in all future sessions. Persisted as one entry in
/// <c>approval-rules.json</c>. Only exact-argument grants exist by design — there is no
/// "any arguments" rule (see the decision log in MyAssistant-DESIGN.html).
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
