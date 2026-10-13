// Copyright (c) Microsoft. All rights reserved.

using Harness.ConsoleReactiveComponents;
using Harness.Shared.Console.ToolFormatters;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MyAssistant.ApprovalPolicy;

namespace Harness.Shared.Console.Observers;

/// <summary>
/// Collects <see cref="ToolApprovalRequestContent"/> items during the response stream,
/// displays approval-needed notifications inline, and after the stream completes returns
/// one <see cref="ChoiceFollowUpQuestion"/> per pending approval request. Each question's
/// continuation produces a separate <see cref="ChatMessage"/> carrying the approval
/// response content.
/// </summary>
/// <remarks>
/// Two modes, selected by whether a durable <see cref="ApprovalRuleStore"/> is supplied:
/// <list type="bullet">
/// <item><description><b>Session-scoped (no store):</b> the original harness behavior — four
/// choices including the MAF harness's session-scoped "Always approve" variants that die with
/// the session.</description></item>
/// <item><description><b>Durable (store supplied):</b> "Always approve" means <i>forever</i>.
/// There is no "any arguments" option; a durable grant is always exact-args. Choosing "Always
/// approve" triggers a deliberate second confirmation step before a rule is written to the
/// store, and the user is told which file to edit to revoke it.</description></item>
/// </list>
/// </remarks>
public sealed class ToolApprovalObserver : ConsoleObserver
{
    private readonly List<ToolApprovalRequestContent> _approvalRequests = [];
    private readonly IReadOnlyList<ToolCallFormatter> _formatters;
    private readonly ApprovalRuleStore? _durableStore;
    private readonly HumanOnlyPolicy? _humanOnlyPolicy;

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolApprovalObserver"/> class.
    /// </summary>
    /// <param name="formatters">Optional list of tool formatters. When <see langword="null"/>,
    /// the default formatters from <see cref="ToolCallFormatter.BuildDefaultToolFormatters"/> are used.</param>
    /// <param name="durableStore">Optional durable approval store. When supplied, the observer
    /// switches to durable (forever) approval mode; when <see langword="null"/>, it keeps the
    /// original session-scoped behavior.</param>
    /// <param name="humanOnlyPolicy">Optional policy identifying tools whose approvals may never
    /// be persisted. Only consulted in durable mode.</param>
    public ToolApprovalObserver(
        IReadOnlyList<ToolCallFormatter>? formatters = null,
        ApprovalRuleStore? durableStore = null,
        HumanOnlyPolicy? humanOnlyPolicy = null)
    {
        this._formatters = formatters ?? ToolCallFormatter.BuildDefaultToolFormatters();
        this._durableStore = durableStore;
        this._humanOnlyPolicy = humanOnlyPolicy;
    }

    /// <inheritdoc/>
    public override async Task OnContentAsync(IUXStateDriver ux, AIContent content, AIAgent agent, AgentSession session)
    {
        if (content is ToolApprovalRequestContent approvalRequest)
        {
            this._approvalRequests.Add(approvalRequest);
            string toolName = approvalRequest.ToolCall is FunctionCallContent fc
                ? ToolCallFormatter.Format(this._formatters, fc)
                : approvalRequest.ToolCall?.ToString() ?? "unknown";
            await ux.WriteInfoLineAsync($"⚠️ Approval needed: {toolName}", ConsoleColor.Yellow);
        }
    }

    /// <inheritdoc/>
    public override Task<IList<FollowUpAction>?> OnStreamCompleteAsync(
        IUXStateDriver ux,
        AIAgent agent,
        AgentSession session)
    {
        if (this._approvalRequests.Count == 0)
        {
            return Task.FromResult<IList<FollowUpAction>?>(null);
        }

        var actions = new List<FollowUpAction>(this._approvalRequests.Count);
        foreach (var request in this._approvalRequests)
        {
            actions.Add(this.BuildApprovalQuestion(request));
        }

        this._approvalRequests.Clear();
        return Task.FromResult<IList<FollowUpAction>?>(actions);
    }

    /// <summary>
    /// Builds the approval question for a single request, choosing durable or session-scoped
    /// behavior based on whether a durable store was supplied.
    /// </summary>
    /// <param name="request">The pending approval request.</param>
    /// <returns>A choice question whose continuation produces the approval response.</returns>
    private ChoiceFollowUpQuestion BuildApprovalQuestion(ToolApprovalRequestContent request)
    {
        return this._durableStore is null
            ? this.BuildSessionScopedQuestion(request)
            : this.BuildDurableQuestion(request);
    }

    /// <summary>
    /// Builds the original session-scoped approval question (four choices, MAF harness
    /// "Always" variants that live only for the current session).
    /// </summary>
    /// <param name="request">The pending approval request.</param>
    /// <returns>A choice question whose continuation produces the approval response.</returns>
    private ChoiceFollowUpQuestion BuildSessionScopedQuestion(ToolApprovalRequestContent request)
    {
        string toolName = this.DisplayName(request);

        var choices = new List<string>
        {
            "Approve this call",
            "Always approve this tool (any arguments)",
            "Always approve this tool with these arguments",
            "Deny",
        };

        string prompt = $"🔐 Tool approval: {toolName}";

        return new ChoiceFollowUpQuestion(
            Prompt: prompt,
            Choices: choices,
            AllowCustomText: false,
            Continuation: async (selection, ux) =>
            {
                AIContent response = selection switch
                {
                    "Always approve this tool (any arguments)" => request.CreateAlwaysApproveToolResponse("User chose to always approve this tool"),
                    "Always approve this tool with these arguments" => request.CreateAlwaysApproveToolWithArgumentsResponse("User chose to always approve this tool with these arguments"),
                    "Deny" => request.CreateResponse(approved: false, reason: "User denied"),
                    _ => request.CreateResponse(approved: true, reason: "User approved"),
                };

                string action = selection switch
                {
                    "Always approve this tool (any arguments)" => "✅ Always approved (any args)",
                    "Always approve this tool with these arguments" => "✅ Always approved (these args)",
                    "Deny" => "❌ Denied",
                    _ => "✅ Approved",
                };

                await this.WriteAnswerAsync(ux, prompt, action, denied: selection == "Deny");
                return new ChatMessage(ChatRole.User, [response]);
            });
    }

    /// <summary>
    /// Builds the durable approval question: three choices, where "Always approve" means a
    /// permanent, exact-argument grant gated behind a deliberate confirmation step.
    /// </summary>
    /// <param name="request">The pending approval request.</param>
    /// <returns>A choice question whose continuation produces the approval response.</returns>
    private ChoiceFollowUpQuestion BuildDurableQuestion(ToolApprovalRequestContent request)
    {
        string toolName = this.DisplayName(request);
        const string approveOnce = "Approve once (this call only)";
        const string approveAlways = "Always approve (remember permanently)";
        const string deny = "Deny";

        var choices = new List<string> { approveOnce, approveAlways, deny };
        string prompt = $"🔐 Tool approval: {toolName}";

        return new ChoiceFollowUpQuestion(
            Prompt: prompt,
            Choices: choices,
            AllowCustomText: false,
            Continuation: async (selection, ux) =>
            {
                if (selection == deny)
                {
                    await this.WriteAnswerAsync(ux, prompt, "❌ Denied", denied: true);
                    return new ChatMessage(ChatRole.User, [request.CreateResponse(approved: false, reason: "User denied")]);
                }

                if (selection != approveAlways)
                {
                    await this.WriteAnswerAsync(ux, prompt, "✅ Approved (once)", denied: false);
                    return new ChatMessage(ChatRole.User, [request.CreateResponse(approved: true, reason: "User approved once")]);
                }

                // "Always approve" — only an exact-args FunctionCallContent can be persisted.
                if (request.ToolCall is not FunctionCallContent call)
                {
                    await this.WriteAnswerAsync(ux, prompt, "✅ Approved (once — this call cannot be remembered)", denied: false);
                    return new ChatMessage(ChatRole.User, [request.CreateResponse(approved: true, reason: "User approved once; call not persistable")]);
                }

                // The hard floor: a human-only tool can never become a durable grant.
                if (this._humanOnlyPolicy?.IsPersistable(call.Name) == false)
                {
                    await this.WriteAnswerAsync(ux, prompt, "⚠️ This action always requires approval and can't be remembered — approving once.", denied: false);
                    return new ChatMessage(ChatRole.User, [request.CreateResponse(approved: true, reason: "User approved once; tool is human-only")]);
                }

                // Print the explanation into the scroll area BEFORE the confirm prompt. The
                // bottom list-selection panel only surfaces a single-line title, so a multi-line
                // Prompt would hide everything but its last line — the user must see this first.
                await ux.WriteInfoLineAsync(
                    $"Remember permanently? {call.Name} with these exact arguments will be auto-approved "
                    + $"in all future sessions.{Environment.NewLine}Revoke anytime by editing or deleting the "
                    + $"entry in:{Environment.NewLine}  {this._durableStore!.FilePath}",
                    ConsoleColor.Yellow).ConfigureAwait(false);

                // Deliberate second step: confirm the permanent grant before writing it.
                ux.QueueFollowUpQuestions([this.BuildPersistConfirmQuestion(request, call)]);
                return null;
            });
    }

    /// <summary>
    /// Builds the confirmation question shown after the user chooses "Always approve" in
    /// durable mode. Approving either way allows the current call; only an explicit "yes"
    /// writes a durable rule.
    /// </summary>
    /// <param name="request">The pending approval request.</param>
    /// <param name="call">The function call whose name and arguments define the durable rule.</param>
    /// <returns>A yes/no confirmation question.</returns>
    private ChoiceFollowUpQuestion BuildPersistConfirmQuestion(ToolApprovalRequestContent request, FunctionCallContent call)
    {
        const string yes = "Yes, remember permanently";
        const string no = "No, just approve once";

        string filePath = this._durableStore!.FilePath;
        // Keep this single-line: the bottom list-selection panel renders only a one-line title.
        // The full explanation (what this means + revoke path) is written to the scroll area by
        // the caller before this question is queued.
        string prompt = $"Remember {call.Name} with these exact arguments permanently?";

        return new ChoiceFollowUpQuestion(
            Prompt: prompt,
            Choices: [yes, no],
            AllowCustomText: false,
            Continuation: async (selection, ux) =>
            {
                if (selection == yes)
                {
                    string canonicalArgs = ArgCanonicalizer.Canonicalize(call.Arguments);
                    await this._durableStore!.AddRuleAsync(call.Name, canonicalArgs);
                    await this.WriteAnswerAsync(ux, prompt, $"✅ Remembered permanently (revoke in {filePath})", denied: false);
                    return new ChatMessage(ChatRole.User, [request.CreateResponse(approved: true, reason: "User approved and persisted a durable grant")]);
                }

                await this.WriteAnswerAsync(ux, prompt, "✅ Approved (once)", denied: false);
                return new ChatMessage(ChatRole.User, [request.CreateResponse(approved: true, reason: "User approved once")]);
            });
    }

    /// <summary>
    /// Gets the formatted display name for an approval request's tool call.
    /// </summary>
    /// <param name="request">The approval request.</param>
    /// <returns>The formatted tool call for display.</returns>
    private string DisplayName(ToolApprovalRequestContent request)
    {
        return request.ToolCall is FunctionCallContent fc
            ? ToolCallFormatter.Format(this._formatters, fc)
            : request.ToolCall?.ToString() ?? "unknown";
    }

    /// <summary>
    /// Writes the chosen answer back to the output area, coloring denials red and everything
    /// else green.
    /// </summary>
    /// <param name="ux">The UX state driver.</param>
    /// <param name="prompt">The prompt that was answered.</param>
    /// <param name="action">The formatted action label to display.</param>
    /// <param name="denied">Whether the action was a denial (controls the color).</param>
    /// <returns>A task that completes when the line has been written.</returns>
    private async Task WriteAnswerAsync(IUXStateDriver ux, string prompt, string action, bool denied)
    {
        ConsoleColor answerColor = denied ? ConsoleColor.Red : ConsoleColor.Green;
        string formatted = $"🔹 {prompt}\n   └─ {AnsiEscapes.SetForegroundColor(answerColor)}{action}{AnsiEscapes.ResetAttributes}";
        await ux.WriteInfoLineAsync(formatted, ConsoleColor.Gray).ConfigureAwait(false);
    }
}
