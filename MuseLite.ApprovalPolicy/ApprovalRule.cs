using System.Text.Json.Serialization;

namespace MyAssistant.ApprovalPolicy;

// DESIGN TRIP-WIRE — read before changing what a durable grant is bound to.
// Two grant kinds exist:
//   * EXACT (args): binds to tool name + the full canonicalized argument set. No target content
//     beyond the args is captured.
//   * SUBSET (matchArgs): binds to tool name + a NAMED subset of args; all other args are free.
//     This deliberately lets content vary (e.g. send_email fixed to a recipient, subject/body
//     free). Honored ONLY for tools the store is told are subset-eligible, and never writable
//     through the UI — a subset rule is a deliberate hand edit of approval-rules.json.
// The content-snapshot-hash guard once considered here is for a DIFFERENT risk: a tool mutating/
// destroying a PRE-EXISTING external target its args don't describe (e.g. delete_note by id),
// where the target can drift between grant and action (TOCTOU). A subset rule has no such external
// target — the "target" is entirely in the args — so that guard does not apply; the governing
// mitigations for subset rules are recipient/value-scoping and full-argument auditing of every
// auto-approval. If a future tool DOES mutate a drift-prone external target, reconsider the
// snapshot-hash guard before granting it any durable approval.

/// <summary>
/// A single durable approval grant. Persisted as one entry in <c>approval-rules.json</c> and
/// read by the auto-approval predicate. A rule is one of two kinds:
/// an <b>exact</b> grant (<see cref="CanonicalArguments"/> set) auto-approves a call whose full
/// canonicalized arguments match exactly; a <b>subset</b> grant (<see cref="MatchArguments"/> set)
/// auto-approves a call whose named subset of arguments matches, leaving all other arguments free.
/// Exactly one of the two is meaningful on a given rule. There is still no blanket "any arguments"
/// rule: a subset grant with no constraints never matches.
/// </summary>
public sealed record ApprovalRule
{
    /// <summary>
    /// Gets the registered tool name this grant applies to (e.g. <c>record_note</c>).
    /// Matches <c>FunctionCallContent.Name</c> seen by the auto-approval predicate.
    /// </summary>
    [JsonPropertyName("tool")]
    public required string ToolName { get; init; }

    /// <summary>
    /// Gets the canonicalized argument string an <b>exact</b> grant requires the call to match
    /// exactly. Produced by <see cref="ArgCanonicalizer.Canonicalize"/> so comparison is stable
    /// across runs. <see langword="null"/> on a subset grant.
    /// </summary>
    [JsonPropertyName("args")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CanonicalArguments { get; init; }

    // Subset grants are an optional capability a consumer turns on via ApprovalRuleStore's
    // subsetEligibleTools. As of 2026-11-03 no consumer in this solution declares any subset-eligible
    // tool, so this path is currently DORMANT — intentionally retained as a general "auto-approve when
    // an argument equals a value" mechanism, not removed.

    /// <summary>
    /// Gets the named argument constraints a <b>subset</b> grant requires: each key must be present
    /// in the call with a canonically equal value; all other call arguments are ignored. An empty
    /// or <see langword="null"/> value is not a subset grant (no blanket rule). Hand-authored only;
    /// never written by the application.
    /// </summary>
    [JsonPropertyName("matchArgs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, object?>? MatchArguments { get; init; }

    /// <summary>
    /// Gets the UTC timestamp at which the user granted this durable approval, or
    /// <see langword="null"/> for a hand-authored rule that omits it (informational only — never
    /// used in matching).
    /// </summary>
    [JsonPropertyName("grantedUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? GrantedUtc { get; init; }
}
