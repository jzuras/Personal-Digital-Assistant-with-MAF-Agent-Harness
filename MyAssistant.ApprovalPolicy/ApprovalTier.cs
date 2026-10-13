namespace MyAssistant.ApprovalPolicy;

/// <summary>
/// The policy tier that governs how a tool/action class is approved. Borrowed from the
/// OpenAI "Dots" policy shape and the MyAssistant design (see MyAssistant-DESIGN.html).
/// </summary>
public enum ApprovalTier
{
    /// <summary>
    /// Auto-approve without prompting. This is the tier a durable grant promotes an exact
    /// call to: a matching rule in the <see cref="ApprovalRuleStore"/> fires silently.
    /// </summary>
    ActSilently,

    /// <summary>
    /// Surface an approval prompt every time. The default tier for any gated tool with no
    /// durable rule.
    /// </summary>
    AskFirst,

    /// <summary>
    /// Never auto-approvable; always a human decision. The non-overridable floor for
    /// money movement, credential changes, and irreversible/outward actions. A
    /// <see cref="HumanOnly"/> action can never be promoted to <see cref="ActSilently"/>.
    /// </summary>
    HumanOnly,
}
