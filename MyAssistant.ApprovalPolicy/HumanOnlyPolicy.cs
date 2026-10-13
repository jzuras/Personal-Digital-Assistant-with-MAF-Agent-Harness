namespace MyAssistant.ApprovalPolicy;

/// <summary>
/// The non-overridable floor: a set of tool names whose approvals may never become durable.
/// A <see cref="HumanOnly"/> tool is always a fresh human decision and can never be promoted
/// to <see cref="ApprovalTier.ActSilently"/>, no matter what the user clicks.
/// </summary>
/// <remarks>
/// This is a semantics-blind stand-in for Muse/Dots' hardcoded "purchases are never
/// auto-approvable" class: rather than classifying actions, we rely on the tool author to
/// list a tool here (next to its <c>requireApproval</c> gating). For Phase 1 the set is
/// empty because no money/credential tool exists yet. See the decision log in
/// MyAssistant-DESIGN.html.
/// </remarks>
public sealed class HumanOnlyPolicy
{
    /// <summary>
    /// Gets the set of tool names that can never receive a durable approval.
    /// </summary>
    private HashSet<string> NonPersistableTools { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="HumanOnlyPolicy"/> class.
    /// </summary>
    /// <param name="humanOnlyToolNames">The tool names that must always require a human
    /// decision, or <see langword="null"/> for none.</param>
    public HumanOnlyPolicy(IEnumerable<string>? humanOnlyToolNames = null)
    {
        this.NonPersistableTools = humanOnlyToolNames is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(humanOnlyToolNames, StringComparer.Ordinal);
    }

    /// <summary>
    /// Determines whether a tool's approval is allowed to be persisted durably.
    /// </summary>
    /// <param name="toolName">The registered tool name.</param>
    /// <returns><see langword="true"/> if a durable grant may be created for this tool;
    /// <see langword="false"/> if it is human-only.</returns>
    public bool IsPersistable(string toolName)
    {
        return !this.NonPersistableTools.Contains(toolName);
    }

    /// <summary>
    /// Gets the policy tier for a tool.
    /// </summary>
    /// <param name="toolName">The registered tool name.</param>
    /// <returns><see cref="ApprovalTier.HumanOnly"/> for a listed tool; otherwise
    /// <see cref="ApprovalTier.AskFirst"/>.</returns>
    public ApprovalTier GetTier(string toolName)
    {
        return this.NonPersistableTools.Contains(toolName)
            ? ApprovalTier.HumanOnly
            : ApprovalTier.AskFirst;
    }
}
