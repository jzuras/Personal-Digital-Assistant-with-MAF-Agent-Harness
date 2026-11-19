using System.Text.Json;

namespace MyAssistant.ApprovalPolicy;

/// <summary>
/// The durable, project-scoped store of exact-argument approval grants. Backed by a pluggable
/// <see cref="IPolicyTextStore"/> (a local file in dev, a blob in the cloud); the store's rule
/// document is the revocation target. The read side is a single predicate
/// (<see cref="IsApprovedAsync"/>) consulted on every gated tool call, including a new session's
/// first call — which is what makes grants cross sessions. The write side
/// (<see cref="AddRuleAsync"/>) is called when the user deliberately chooses to remember an
/// approval permanently.
/// </summary>
/// <remarks>
/// Rules are deliberately NOT cached: every operation loads the document fresh from the backing
/// store, so a grant written by another process
/// and a hand edit or revocation of the document both take effect on the very next gated
/// call — not at the next restart. The document is tiny and gated calls are rare, so the extra
/// read is negligible. Within the process, all access is serialized by a semaphore so the
/// predicate and the write side never race.
/// </remarks>
public sealed class ApprovalRuleStore
{
    /// <summary>
    /// The backing store that persists the rule document.
    /// </summary>
    private IPolicyTextStore Store { get; }

    /// <summary>
    /// The name of the rule document within the store.
    /// </summary>
    private string Name { get; }

    /// <summary>
    /// Gets a human-readable description of where the rule document lives. Surfaced so the UI can
    /// tell the user where to go to revoke a grant.
    /// </summary>
    public string RevocationLocation => this.Store.DescribeLocation(this.Name);

    /// <summary>
    /// Gets the audit log that records silent auto-approvals, or <see langword="null"/> when
    /// auditing is disabled.
    /// </summary>
    private IApprovalAuditLog? AuditLog { get; }

    // DORMANT as of 2026-11-03: no consumer in this solution passes subsetEligibleTools, so the subset
    // branch in Matches never activates. Retained deliberately as a general capability — pass tool
    // name(s) to the constructor to re-activate it.

    /// <summary>
    /// Gets the set of tool names for which <b>subset</b> grants
    /// (<see cref="ApprovalRule.MatchArguments"/>) are honored. A subset rule naming any tool not in
    /// this set is ignored, so a stray hand edit can never loosen approval for a tool that was never
    /// meant to allow partial-argument grants. Exact grants are unaffected and work for every tool.
    /// </summary>
    private HashSet<string> SubsetEligibleTools { get; }

    /// <summary>
    /// Serializes all load/read/write access to the backing document within this process.
    /// </summary>
    private SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>
    /// The serializer options used when writing the rule file (indented for human editing).
    /// </summary>
    private static JsonSerializerOptions WriteOptions { get; } = new() { WriteIndented = true };

    /// <summary>
    /// Initializes a new instance of the <see cref="ApprovalRuleStore"/> class.
    /// </summary>
    /// <param name="store">The backing store for the rule document.</param>
    /// <param name="name">The name of the rule document within the store (e.g. <c>approval-rules.json</c>).</param>
    /// <param name="auditLog">Optional audit log that records silent auto-approvals.</param>
    /// <param name="subsetEligibleTools">Tool names for which subset grants
    /// (<see cref="ApprovalRule.MatchArguments"/>) are honored, or <see langword="null"/> for none
    /// (exact grants still work for every tool).</param>
    public ApprovalRuleStore(IPolicyTextStore store, string name, IApprovalAuditLog? auditLog = null, IEnumerable<string>? subsetEligibleTools = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        this.Store = store;
        this.Name = name;
        this.AuditLog = auditLog;
        this.SubsetEligibleTools = subsetEligibleTools is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(subsetEligibleTools, StringComparer.Ordinal);
    }

    /// <summary>
    /// Determines whether a tool call has a durable grant — either an exact-argument grant matching
    /// the full canonicalized arguments, or a subset grant whose named arguments all match. When it
    /// does, records an audit entry (carrying the call's <em>full</em> canonical arguments, so a
    /// subset auto-approval still logs exactly what was acted on) before returning.
    /// </summary>
    /// <param name="toolName">The registered tool name.</param>
    /// <param name="arguments">The tool call's arguments (e.g. <c>FunctionCallContent.Arguments</c>),
    /// or <see langword="null"/> when the call has none. Canonicalized internally so the store is the
    /// single source of the matching key.</param>
    /// <returns><see langword="true"/> if a matching durable rule exists; otherwise <see langword="false"/>.</returns>
    public async Task<bool> IsApprovedAsync(string toolName, IEnumerable<KeyValuePair<string, object?>>? arguments)
    {
        await this.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            List<ApprovalRule> rules = await this.LoadAsync().ConfigureAwait(false);

            // Materialize once: the same argument map feeds both exact canonicalization (and the audit
            // record) and per-rule subset extraction, so a single-pass enumerable is never read twice.
            Dictionary<string, object?> argumentMap = ApprovalRuleStore.ToArgumentMap(arguments);
            string fullCanonical = ArgCanonicalizer.Canonicalize(argumentMap);

            bool match = rules.Any(rule => this.Matches(rule, toolName, fullCanonical, argumentMap));
            if (match && this.AuditLog is not null)
            {
                await this.AuditLog.RecordAutoApprovalAsync(toolName, fullCanonical, DateTimeOffset.UtcNow).ConfigureAwait(false);
            }

            return match;
        }
        finally
        {
            this.Gate.Release();
        }
    }

    /// <summary>
    /// Adds a durable grant for a tool invoked with the given canonical arguments. A no-op if
    /// an identical rule already exists.
    /// </summary>
    /// <param name="toolName">The registered tool name.</param>
    /// <param name="canonicalArguments">The canonical arguments from <see cref="ArgCanonicalizer.Canonicalize"/>.</param>
    /// <returns>A task that completes when the rule has been persisted.</returns>
    public async Task AddRuleAsync(string toolName, string canonicalArguments)
    {
        await this.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            List<ApprovalRule> rules = await this.LoadAsync().ConfigureAwait(false);
            if (rules.Any(rule => ApprovalRuleStore.MatchesExact(rule, toolName, canonicalArguments)) is true)
            {
                return;
            }

            rules.Add(new ApprovalRule
            {
                ToolName = toolName,
                CanonicalArguments = canonicalArguments,
                GrantedUtc = DateTimeOffset.UtcNow,
            });

            await this.SaveAsync(rules).ConfigureAwait(false);
        }
        finally
        {
            this.Gate.Release();
        }
    }

    /// <summary>
    /// Removes a durable grant matching the given tool and canonical arguments, if present.
    /// </summary>
    /// <param name="toolName">The registered tool name.</param>
    /// <param name="canonicalArguments">The canonical arguments from <see cref="ArgCanonicalizer.Canonicalize"/>.</param>
    /// <returns>A task that completes when any matching rule has been removed.</returns>
    public async Task RemoveRuleAsync(string toolName, string canonicalArguments)
    {
        await this.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            List<ApprovalRule> rules = await this.LoadAsync().ConfigureAwait(false);
            int removed = rules.RemoveAll(rule => ApprovalRuleStore.MatchesExact(rule, toolName, canonicalArguments));
            if (removed > 0)
            {
                await this.SaveAsync(rules).ConfigureAwait(false);
            }
        }
        finally
        {
            this.Gate.Release();
        }
    }

    /// <summary>
    /// Gets a snapshot of all durable rules currently in the store.
    /// </summary>
    /// <returns>A copy of the current rules.</returns>
    public async Task<IReadOnlyList<ApprovalRule>> GetRulesAsync()
    {
        await this.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            List<ApprovalRule> rules = await this.LoadAsync().ConfigureAwait(false);
            return rules.ToList();
        }
        finally
        {
            this.Gate.Release();
        }
    }

    // DESIGN TRIP-WIRE — read before changing matching/approval semantics.
    // An EXACT hit (tool + full canonical args) fires a durable "yes" silently, with no check that
    // the real target still looks the way it did at grant time. A SUBSET hit (tool + named args,
    // the rest free) fires silently too, and additionally lets the free arguments be anything — so
    // it is restricted to subset-eligible tools and relies on value-scoping (e.g. a fixed
    // recipient) plus full-argument auditing, NOT a target check. If a future tool mutates/destroys
    // an external target its args don't fully describe, consider the deferred content-snapshot hash
    // guard (mismatch -> re-prompt, don't silently act) before granting it any durable approval.

    /// <summary>
    /// Determines whether a rule approves a call — an exact grant against the full canonical
    /// arguments, or a subset grant whose named arguments all match (other arguments ignored).
    /// Subset grants are honored only for <see cref="SubsetEligibleTools"/>.
    /// </summary>
    /// <param name="rule">The rule to test.</param>
    /// <param name="toolName">The registered tool name.</param>
    /// <param name="fullCanonical">The call's full canonicalized arguments (for an exact grant).</param>
    /// <param name="argumentMap">The call's arguments by name (for a subset grant).</param>
    /// <returns><see langword="true"/> if the rule approves the call; otherwise <see langword="false"/>.</returns>
    private bool Matches(ApprovalRule rule, string toolName, string fullCanonical, Dictionary<string, object?> argumentMap)
    {
        if (string.Equals(rule.ToolName, toolName, StringComparison.Ordinal) is false)
        {
            return false;
        }

        // Subset grant: every named constraint must be present in the call and canonically equal;
        // all other call arguments are ignored. Honored only for subset-eligible tools, and an empty
        // constraint set is never a match (no blanket "any arguments" grant).
        if (rule.MatchArguments is { Count: > 0 } constraints)
        {
            if (this.SubsetEligibleTools.Contains(toolName) is false)
            {
                return false;
            }

            var callSubset = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (string key in constraints.Keys)
            {
                if (argumentMap.TryGetValue(key, out object? value) is false)
                {
                    return false;
                }

                callSubset[key] = value;
            }

            return string.Equals(
                ArgCanonicalizer.Canonicalize(callSubset),
                ArgCanonicalizer.Canonicalize(constraints),
                StringComparison.Ordinal);
        }

        // Exact grant: full canonical arguments must match. Unchanged behavior.
        return rule.CanonicalArguments is not null
            && string.Equals(rule.CanonicalArguments, fullCanonical, StringComparison.Ordinal);
    }

    /// <summary>
    /// Determines whether a rule is an exact grant matching the tool name and canonical arguments.
    /// Subset grants are never matched here — the write and remove paths deal only with exact,
    /// application-written grants.
    /// </summary>
    /// <param name="rule">The rule to test.</param>
    /// <param name="toolName">The registered tool name.</param>
    /// <param name="canonicalArguments">The canonical arguments.</param>
    /// <returns><see langword="true"/> if the rule is an exact match; otherwise <see langword="false"/>.</returns>
    private static bool MatchesExact(ApprovalRule rule, string toolName, string canonicalArguments)
    {
        return rule.MatchArguments is null or { Count: 0 }
            && string.Equals(rule.ToolName, toolName, StringComparison.Ordinal)
            && string.Equals(rule.CanonicalArguments, canonicalArguments, StringComparison.Ordinal);
    }

    /// <summary>
    /// Materializes a tool call's arguments into a case-sensitive map, used for both full
    /// canonicalization and per-rule subset extraction. Never <see langword="null"/>.
    /// </summary>
    /// <param name="arguments">The tool call arguments, or <see langword="null"/> when none.</param>
    /// <returns>A dictionary of the arguments by name (empty when <paramref name="arguments"/> is null).</returns>
    private static Dictionary<string, object?> ToArgumentMap(IEnumerable<KeyValuePair<string, object?>>? arguments)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (arguments is not null)
        {
            foreach (KeyValuePair<string, object?> pair in arguments)
            {
                map[pair.Key] = pair.Value;
            }
        }

        return map;
    }

    /// <summary>
    /// Loads the rules fresh from the backing store. The caller must hold <see cref="Gate"/>.
    /// Deliberately uncached (see the class remarks): reading per operation is what lets a grant
    /// written by another process, or a hand edit/revocation of the document, take effect on the
    /// very next gated call.
    /// </summary>
    /// <returns>The current list of rules (never <see langword="null"/>).</returns>
    private async Task<List<ApprovalRule>> LoadAsync()
    {
        string? json = await this.Store.ReadAsync(this.Name).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(json) is true
            ? []
            : JsonSerializer.Deserialize<List<ApprovalRule>>(json) ?? [];
    }

    /// <summary>
    /// Writes the rules to the backing store. The caller must hold <see cref="Gate"/>.
    /// </summary>
    /// <param name="rules">The rules to persist.</param>
    /// <returns>A task that completes when the document has been written.</returns>
    private async Task SaveAsync(List<ApprovalRule> rules)
    {
        string json = JsonSerializer.Serialize(rules, ApprovalRuleStore.WriteOptions);
        await this.Store.WriteAsync(this.Name, json).ConfigureAwait(false);
    }
}
