namespace MyAssistant.ApprovalPolicy;

// DESIGN TRIP-WIRE: this floor only decides "may this be auto-approved?". In an unattended
// context (no human to answer a prompt), that is not enough — also gate the toolset by context
// so dangerous tools aren't reachable at all.

/// <summary>
/// The non-overridable floor: a set of tool names whose approvals may never become durable.
/// A human-only tool is always a fresh human decision and can never be promoted
/// to <see cref="ApprovalTier.ActSilently"/>, no matter what the user clicks.
/// </summary>
/// <remarks>
/// This is a semantics-blind stand-in for Muse/Dots' hardcoded "purchases are never
/// auto-approvable" class: rather than classifying actions, we rely on the tool author to
/// list a tool here (next to its <c>requireApproval</c> gating). The application's standing
/// floor is <see cref="CreateDefault"/>; the list-taking constructor stays for tests and for
/// consumers that want a custom floor.
/// </remarks>
public sealed class HumanOnlyPolicy
{
    /// <summary>
    /// Gets the application's default human-only floor: the tool names that must always be a
    /// fresh human decision and can never receive a durable grant. Centralized here so every
    /// write entry point that can persist a standing grant (the agent server and the companion
    /// app each expose one) enforces the identical floor — a divergence between them could let one
    /// process persist a grant the other refuses. Add a tool here when it becomes non-persistable.
    /// </summary>
    private static IReadOnlyList<string> DefaultFloorTools { get; } = ["send_email"];

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
    /// Creates a policy carrying the application's standing human-only floor
    /// (<see cref="DefaultFloorTools"/>). Call sites that want the canonical floor use this instead
    /// of spelling the list out, so the floor lives in exactly one place.
    /// </summary>
    /// <returns>A policy whose floor is the application default.</returns>
    public static HumanOnlyPolicy CreateDefault()
    {
        return new HumanOnlyPolicy(HumanOnlyPolicy.DefaultFloorTools);
    }

    /// <summary>
    /// Determines whether a tool's approval is allowed to be persisted durably.
    /// </summary>
    /// <param name="toolName">The registered tool name.</param>
    /// <returns><see langword="true"/> if a durable grant may be created for this tool;
    /// <see langword="false"/> if it is human-only.</returns>
    public bool IsPersistable(string toolName)
    {
        return this.NonPersistableTools.Contains(toolName) is false;
    }

    /// <summary>
    /// Gets the policy tier for a tool.
    /// </summary>
    /// <param name="toolName">The registered tool name.</param>
    /// <returns><see cref="ApprovalTier.HumanOnly"/> for a listed tool; otherwise
    /// <see cref="ApprovalTier.AskFirst"/>.</returns>
    public ApprovalTier GetTier(string toolName)
    {
        return this.NonPersistableTools.Contains(toolName) is true
            ? ApprovalTier.HumanOnly
            : ApprovalTier.AskFirst;
    }
}
