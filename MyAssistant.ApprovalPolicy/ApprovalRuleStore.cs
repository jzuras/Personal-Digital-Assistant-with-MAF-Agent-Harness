using System.Text.Json;

namespace MyAssistant.ApprovalPolicy;

/// <summary>
/// The durable, project-scoped store of exact-argument approval grants. Backed by a plain
/// JSON file the user can open, edit, and delete (that file is the revocation path). The
/// read side is a single predicate (<see cref="IsApprovedAsync"/>) consulted on every gated
/// tool call, including a new session's first call — which is what makes grants cross
/// sessions. The write side (<see cref="AddRuleAsync"/>) is called when the user deliberately
/// chooses to remember an approval permanently.
/// </summary>
/// <remarks>
/// Rules are cached in memory and loaded once per process; a user editing the file by hand
/// takes effect on the next run (per the Phase 1 design). Our own writes refresh the cache
/// immediately. All access is serialized by a semaphore so the predicate and the write side
/// never race.
/// </remarks>
public sealed class ApprovalRuleStore
{
    /// <summary>
    /// Gets the full path of the JSON rule file. Surfaced so the UI can tell the user where
    /// to go to revoke a grant.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Gets the audit log that records silent auto-approvals, or <see langword="null"/> when
    /// auditing is disabled.
    /// </summary>
    private IApprovalAuditLog? AuditLog { get; }

    /// <summary>
    /// Serializes all load/read/write access to the cache and file.
    /// </summary>
    private SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>
    /// The in-memory cache of rules, or <see langword="null"/> until first loaded.
    /// </summary>
    private List<ApprovalRule>? Cache { get; set; }

    /// <summary>
    /// The serializer options used when writing the rule file (indented for human editing).
    /// </summary>
    private static JsonSerializerOptions WriteOptions { get; } = new() { WriteIndented = true };

    /// <summary>
    /// Initializes a new instance of the <see cref="ApprovalRuleStore"/> class.
    /// </summary>
    /// <param name="filePath">The full path of the JSON rule file (e.g. <c>working\approval-rules.json</c>).</param>
    /// <param name="auditLog">Optional audit log that records silent auto-approvals.</param>
    public ApprovalRuleStore(string filePath, IApprovalAuditLog? auditLog = null)
    {
        this.FilePath = filePath;
        this.AuditLog = auditLog;
    }

    /// <summary>
    /// Determines whether a tool call with the given canonical arguments has a durable grant.
    /// When it does, records an audit entry before returning.
    /// </summary>
    /// <param name="toolName">The registered tool name.</param>
    /// <param name="canonicalArguments">The canonical arguments from <see cref="ArgCanonicalizer.Canonicalize"/>.</param>
    /// <returns><see langword="true"/> if a matching durable rule exists; otherwise <see langword="false"/>.</returns>
    public async Task<bool> IsApprovedAsync(string toolName, string canonicalArguments)
    {
        await this.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            List<ApprovalRule> rules = await this.EnsureLoadedAsync().ConfigureAwait(false);
            bool match = rules.Any(rule => ApprovalRuleStore.Matches(rule, toolName, canonicalArguments));
            if (match && this.AuditLog is not null)
            {
                await this.AuditLog.RecordAutoApprovalAsync(toolName, canonicalArguments, DateTimeOffset.UtcNow).ConfigureAwait(false);
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
            List<ApprovalRule> rules = await this.EnsureLoadedAsync().ConfigureAwait(false);
            if (rules.Any(rule => ApprovalRuleStore.Matches(rule, toolName, canonicalArguments)))
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
            List<ApprovalRule> rules = await this.EnsureLoadedAsync().ConfigureAwait(false);
            int removed = rules.RemoveAll(rule => ApprovalRuleStore.Matches(rule, toolName, canonicalArguments));
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
            List<ApprovalRule> rules = await this.EnsureLoadedAsync().ConfigureAwait(false);
            return rules.ToList();
        }
        finally
        {
            this.Gate.Release();
        }
    }

    /// <summary>
    /// Determines whether a rule matches a tool name and canonical arguments exactly.
    /// </summary>
    /// <param name="rule">The rule to test.</param>
    /// <param name="toolName">The registered tool name.</param>
    /// <param name="canonicalArguments">The canonical arguments.</param>
    /// <returns><see langword="true"/> if the rule matches; otherwise <see langword="false"/>.</returns>
    private static bool Matches(ApprovalRule rule, string toolName, string canonicalArguments)
    {
        return string.Equals(rule.ToolName, toolName, StringComparison.Ordinal)
            && string.Equals(rule.CanonicalArguments, canonicalArguments, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ensures the in-memory cache is populated from disk. The caller must hold <see cref="Gate"/>.
    /// </summary>
    /// <returns>The cached list of rules (never <see langword="null"/>).</returns>
    private async Task<List<ApprovalRule>> EnsureLoadedAsync()
    {
        if (this.Cache is not null)
        {
            return this.Cache;
        }

        if (!File.Exists(this.FilePath))
        {
            this.Cache = [];
            return this.Cache;
        }

        await using FileStream stream = File.OpenRead(this.FilePath);
        List<ApprovalRule>? loaded = await JsonSerializer.DeserializeAsync<List<ApprovalRule>>(stream).ConfigureAwait(false);
        this.Cache = loaded ?? [];
        return this.Cache;
    }

    /// <summary>
    /// Writes the rules to disk and refreshes the cache. The caller must hold <see cref="Gate"/>.
    /// </summary>
    /// <param name="rules">The rules to persist.</param>
    /// <returns>A task that completes when the file has been written.</returns>
    private async Task SaveAsync(List<ApprovalRule> rules)
    {
        string? directory = Path.GetDirectoryName(this.FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using (FileStream stream = File.Create(this.FilePath))
        {
            await JsonSerializer.SerializeAsync(stream, rules, ApprovalRuleStore.WriteOptions).ConfigureAwait(false);
        }

        this.Cache = rules;
    }
}
