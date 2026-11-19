using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using MuseLite.AgenticUI;
using MyAssistant.ApprovalPolicy;
using MyAssistant.DurableContext;
using System.Text.Json;

namespace MuseLite;

#pragma warning disable MAAI001 // Harness composition APIs (AsHarnessAgent / FileAccessProvider / session store) are experimental.

/// <summary>
/// Composes the assistant server: chat client, durable stores, approval policy, email tools,
/// compaction history provider and the harness agent, then registers the durable session store and
/// returns the pieces Main needs (see <see cref="AssistantServer"/>).
/// </summary>
internal static class ServerComposition
{
    // Context-window compaction bounds. A single durable context grows unbounded, so compaction is
    // enabled; these two values parameterize it. They are wired as the history provider's reducer (a
    // single ContextWindowCompactionStrategy over the live history — see the ChatHistoryProvider below),
    // NOT via the harness's MaxContextWindowTokens/MaxOutputTokens knobs. Compaction edits only the
    // message history, not ChatOptions.Instructions — so standing safety clauses stay in the ~0%-decay
    // system channel, and durable facts live in working/ files outside the transcript: summarization can
    // cost conversational nuance, but not memory or constraints.
    //
    // Deliberately a single fixed CONSTANT pair (not config) for now, applied regardless of which
    // provider MuseLite:UseOpenRouter selects. Provider context windows differ widely (OpenRouter's
    // free models are small; Azure OpenAI is large), so one pair is a compromise rather than optimal
    // for either. TODO: make these model-dependent and move to config alongside UseOpenRouter, then
    // eyeball reducer aggressiveness on a long thread. MaxContextWindowTokens is the total budget at
    // which compaction kicks in; MaxOutputTokens is reserved headroom for the response. Err small
    // (compact early) rather than large (risk overflow).
    private const int DefaultMaxContextWindowTokens = 800_000; // compaction trigger window
    private const int DefaultMaxOutputTokens = 120_000; 

    /// <summary>
    /// Builds and wires the assistant server.
    /// </summary>
    /// <param name="builder">The web application builder.</param>
    /// <param name="jsonOptions">The serializer options for the agentic-UI tools.</param>
    /// <returns>The composed <see cref="AssistantServer"/>.</returns>
    public static AssistantServer Create(WebApplicationBuilder builder, JsonSerializerOptions jsonOptions)
    {
        IChatClient chatClient = ChatClientFactory.Create(builder);

        string name = "AgenticUIAgent";

        // DESIGN TRIP-WIRE — read before adding a new run context or a context-specific toolset.
        // The toolset is handed to the agent here, and it is the SAME toolset for every context. Two
        // kinds of run now exist: attended (a user is present to answer approval prompts) and unattended
        // scheduled routine runs (no human present). A run is recognized as unattended by its reserved
        // transient session id — the hosting platform's routine invoker, not a real client — see
        // TransientSessionKeys / MyAgentSessionStore.
        //
        // The human-only floor already protects unattended runs STRUCTURALLY, with no per-context toolset
        // required: a floored tool (e.g. send_email to an arbitrary recipient) can never be auto-approved,
        // so with no human to answer its prompt an unattended run cannot carry it out (it stalls or fails).
        // send_self_email (structurally owner-only) IS auto-approved, the task instructions steer scheduled
        // runs to it, and EmailTools records every send to email-send.log for accountability.
        //
        // Handing unattended runs a DIFFERENT prompt or a restricted read+notify toolset was considered
        // but is deliberately NOT done, and may never be needed — the floor already covers the safety
        // case. Revisit only if an unattended run attempting a floored call (and stalling on the
        // unanswerable prompt) becomes a real problem; then gate the toolset by context HERE, not by
        // trusting the model's tool choice.

        // Durable storage — one config choice (DurableContext:Backend) drives every durable store. The
        // composition (backend flag, Blob client build, container/directory selection, and the
        // agent-inaccessible policy store split) is centralized in DurableStorage, so any process that
        // touches the durable state resolves the identical stores and cannot drift on where it lives.
        DurableStorage storage = DurableStorage.Create(builder.Configuration, AppContext.BaseDirectory);
        IPolicyTextStore policyStore = storage.PolicyStore;

        // Durable approval policy. A call with no stored rule prompts; a call whose
        // exact canonicalized args match a rule in the policy store's approval-rules.json is auto-approved silently
        // (and audited). This is the read side — the same ApprovalRuleStore every writer persists to, so a
        // grant made by any process is honored here. The client's "remember permanently" choice writes to this
        // same store via the x-client-op "approvals" dispatch on /invocations (see InvocationEndpoints).
        var approvalAuditLog = new ApprovalAuditLog(policyStore, "approval-audit.log");
        // No tool is declared subset-eligible, so the store's argument-subset matching is currently
        // DORMANT (2026-11-03). It was built for a recipient-scoped "always OK to email myself" grant,
        // then superseded by the two-tool split below: a structurally owner-only send_self_email that is
        // auto-approved outright, plus a send_email that is floored (always a fresh human decision).
        // The subset capability is kept — not removed — as a general "auto-approve when an argument
        // equals a value" mechanism (e.g. a future small set of trusted recipients). To re-activate it,
        // pass the eligible tool name(s) here as subsetEligibleTools.
        // NOTE: this DORMANT status is about the STORE's subset matching only. A separate subset-style
        // auto-approval does exist — the tasks/-write predicate in AutoApprovalRules (IsTaskDirectoryWrite,
        // matching on just the fileName argument) used by unattended routine runs — but that is a bespoke
        // code predicate, not this store feature.
        var approvalStore = new ApprovalRuleStore(
            policyStore,
            "approval-rules.json",
            approvalAuditLog);

        // The human-only floor comes from HumanOnlyPolicy.CreateDefault() — one source shared by every
        // write entry point so the floor can never drift.
        // send_email is on that floor: sending to an arbitrary recipient is outward-facing and irreversible,
        // so it is always a fresh human decision and can never be auto-approved or remembered (a
        // durable/standing grant for it is refused). It still works in attended chat — it prompts and the
        // human approves each send. send_self_email is deliberately NOT floored: it can only reach the owner
        // (recipient fixed in code), so it is auto-approved (see AutoApprovalRules).
        var humanOnlyPolicy = HumanOnlyPolicy.CreateDefault();

        // Outbound email sender: app-only Microsoft Graph sendMail, configured from the MuseLite:Email section
        // (real values come from Azure App Configuration; the ClientSecret is a secret and lives only there).
        // All mail is sent FROM MuseLite:Email:SenderAddress; send_self_email also sends TO it, so its recipient
        // is fixed here and is never a model-visible argument (the tool can only reach the owner). Each send is
        // appended to email-send.log in the (agent-inaccessible) policy store — send_self_email is auto-approved
        // (no approval-audit entry), so the tool itself carries the accountability trail. When the credentials
        // are absent, the tools return a clear "not configured" message instead of attempting a send.
        var emailTools = new EmailTools(
            builder.Configuration["MuseLite:Email:TenantId"],
            builder.Configuration["MuseLite:Email:ClientId"],
            builder.Configuration["MuseLite:Email:ClientSecret"],
            builder.Configuration["MuseLite:Email:SenderAddress"],
            policyStore,
            "email-send.log");

        // Chat-history provider hoisted to a local so the /history hydration endpoint can read the stored
        // transcript via GetMessages(session) (see the MapGet in Main). The compaction wiring is documented
        // at the use site in HarnessAgentOptions below.
        var historyProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
        {
            ChatReducer = new ContextWindowCompactionStrategy(
                DefaultMaxContextWindowTokens,
                DefaultMaxOutputTokens,
                ContextWindowCompactionStrategy.DefaultToolEvictionThreshold,
                ContextWindowCompactionStrategy.DefaultTruncationThreshold).AsChatReducer(),
            ReducerTriggerEvent = InMemoryChatHistoryProviderOptions.ChatReducerTriggerEvent.BeforeMessagesRetrieval,
        });

        // Small logger factory for the opt-in debug diagnostics (request dump + stream-error surfacing),
        // bound to the app's Logging configuration so they obey the standard log levels: silent at the
        // default Information level, enabled by setting "MuseLite.AgenticUI" to Debug. Built here
        // because the chat-client wrappers are composed before the app (and its ILoggerFactory) exists. App
        // lifetime — intentionally not disposed.
        ILoggerFactory diagnosticLoggerFactory = LoggerFactory.Create(logging =>
        {
            logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
            logging.AddConsole();
        });

        // Debug-level request dump: logs the final outbound history just before it reaches the model. Wrapped
        // INSIDE the stripper below, so when enabled it shows the post-strip request. Off unless the Logging
        // category is raised to Debug.
        chatClient = new RequestDumpChatClient(chatClient, diagnosticLoggerFactory.CreateLogger<RequestDumpChatClient>());

        // Strip hosted-tool call/result items from the replayed history. Under stored-output-disabled
        // (client-managed) history the whole transcript is resent as input each turn, and hosted-tool items
        // (web search, etc.) are not valid input items — replaying them fails the Responses API with HTTP 400
        // invalid_payload. This is the permanent fix. See HostedToolHistoryStrippingChatClient.
        chatClient = new HostedToolHistoryStrippingChatClient(chatClient);

        // <create_agent>
        // Turn the chat client into a HarnessAgent. AsHarnessAgent pre-configures function invocation,
        // per-service-call chat history persistence, TodoProvider, AgentModeProvider, and web search.
        // We add the assistant's instructions and tools (see ChatOptions below).
        AIAgent baseAgent = chatClient.AsHarnessAgent(new HarnessAgentOptions
        {
            Name = name,

            // Durable, inspectable memory via the file_access tools in working/
            // (identity/preferences/memories), with the built-in per-session file memory
            // disabled (a single inspectable store). Local dev uses the filesystem; the Blob backend routes
            // the same store to a working-memory container so the agent's memory survives restarts and is
            // shared across hosted sessions.
            FileAccessStore = storage.WorkingStore,
            DisableFileMemory = true,

            // TodoProvider OFF for both providers. Its
            // per-turn trailing user-role "### Current todo list" injection becomes the model's most-recent
            // input and derails plain chat on BOTH the small OpenRouter model (contributes to a gated-tool
            // retry loop) and Azure gpt-5.4 (answers the injection instead of the user's message). Unused by
            // this assistant; re-enable only with a non-derailing injection mechanism.
            DisableTodoProvider = true,

            // Plan/execute mode is OFF for both providers. AgentModeProvider is a harness default aimed at
            // other use cases (coding agents / planners); this assistant never uses it, and it misbehaves
            // on both models (OpenRouter: contributes to the retry loop; Azure: injects plan-mode text and
            // skips the action). Unused + problematic => disabled unconditionally.
            DisableAgentModeProvider = true,

            // Context-window compaction wired as the InMemoryChatHistoryProvider's IChatReducer: ONE history
            // mechanism, operating on the live history. We deliberately do NOT set MaxContextWindowTokens/
            // MaxOutputTokens — that makes the harness run compaction as a SEPARATE standalone
            // CompactionProvider alongside the history provider, and when a message-injecting provider is also
            // active (here: the AG-UI hosting layer) the standalone view FREEZES at the turn-1 snapshot and
            // silently drops the newest user turn. Wiring the SAME ContextWindowCompactionStrategy (same
            // default thresholds the harness would auto-build; token budget from the DefaultMax* constants
            // above) as the provider's reducer avoids the standalone path entirely. This is a framework defect
            // tracked upstream as microsoft/agent-framework #7395 (fix in PR #8838).
            //
            // SIDE EFFECT of dropping MaxOutputTokens: the harness no longer auto-sets
            // ChatOptions.MaxOutputTokens (the DefaultMaxOutputTokens passed to the compaction strategy
            // above only bounds the compaction budget, not model output). We therefore set the
            // model-output cap explicitly on ChatOptions below
            // (MaxOutputTokens = DefaultMaxOutputTokens), restoring the prior response-length limit.
            ChatHistoryProvider = historyProvider,

            // Durable approval policy. Read-only file tools flow freely; a call
            // whose exact canonical args match a stored rule is auto-approved silently (audited); any other
            // gated call prompts. A human-only tool is never auto-approved even if a rule exists (defense in
            // depth). MaxAutoApprovalIterations pins the safety cap on the auto-approval re-invocation loop.
            ToolApprovalAgentOptions = new ToolApprovalAgentOptions
            {
                MaxAutoApprovalIterations = 40,
                AutoApprovalRules =
                [
                    FileAccessProvider.ReadOnlyToolsAutoApprovalRule,
                    async ctx =>
                    {
                        FunctionCallContent call = ctx.FunctionCallContent;
                        if (humanOnlyPolicy.IsPersistable(call.Name) is false)
                        {
                            return false;
                        }

                        return await approvalStore.IsApprovedAsync(call.Name, call.Arguments).ConfigureAwait(false);
                    },

                    // send_self_email is structurally owner-only (recipient fixed in code), so it is
                    // auto-approved outright — no prompt and no stored rule needed. Honor the floor
                    // defensively: if it were ever added to the human-only set, stop auto-approving it.
                    // NOTE: this code predicate does not write the approval audit log; because this tool is
                    // auto-sent, EmailTools itself records every send to working/email-send.log for accountability.
                    ctx => string.Equals(ctx.FunctionCallContent.Name, "send_self_email", StringComparison.Ordinal) is true
                        ? new ValueTask<bool>(humanOnlyPolicy.IsPersistable("send_self_email"))
                        : new ValueTask<bool>(false),

                    // Scheduled (unattended) task runs rewrite their own task files under tasks/ in the
                    // working store, with no human present to answer an approval prompt. Auto-approve the
                    // write-family file_access tools (write / replace / replace_lines — NOT delete) when the
                    // target is under tasks/, honoring the human-only floor defensively, and record each in
                    // the approval audit log — that log is the accountability trail for these unattended
                    // task-file mutations. Read-only file tools are already auto-approved above.
                    async ctx =>
                    {
                        FunctionCallContent taskCall = ctx.FunctionCallContent;
                        if (TaskDirectoryPolicy.IsTaskDirectoryWrite(taskCall) is false
                            || humanOnlyPolicy.IsPersistable(taskCall.Name) is false)
                        {
                            return false;
                        }

                        await approvalAuditLog.RecordAutoApprovalAsync(
                            taskCall.Name,
                            ArgCanonicalizer.Canonicalize(taskCall.Arguments),
                            DateTimeOffset.UtcNow).ConfigureAwait(false);
                        return true;
                    },
                ],
            },

            // Approval-response binding: LEFT AT THE FRAMEWORK DEFAULT (ON). Background: interactive
            // approval over AG-UI once failed on resume with "no matching ToolApprovalResponseContent"
            // because the binding state didn't survive the AG-UI persist/reload (issue #9102), so we had
            // temporarily set DisableApprovalResponseBinding = true. Once MyAgentSessionStore (durable,
            // atomic) was in place the binding was re-enabled and approve -> resume -> execute works on
            // BOTH OpenRouter and Azure — the durable store was the missing prerequisite — regaining PR
            // #8641's server-authoritative approval guarantee. See SERVER-SECURITY-NOTES.md. NOTE: the
            // server is still single-user for a SEPARATE reason (per-caller isolation is off —
            // withIsolation:false); that is unaffected by this. Durable standing auto-approval is also a
            // separate, server-side concern.

            ChatOptions = new ChatOptions
            {
                // Cap the model to a single tool call per response (no parallel tool calls).
                AllowMultipleToolCalls = false,

                Instructions = AssistantInstructions.System,

                // Explicit model-output cap. Restores the max token response limit the harness used to
                // auto-set from HarnessAgentOptions.MaxOutputTokens — which we no longer set (that property is
                // what spawned the standalone compaction provider; see the ChatHistoryProvider comment
                // above). Set here directly so dropping MaxOutputTokens doesn't silently uncap output.
                MaxOutputTokens = DefaultMaxOutputTokens,

                // Example gated tool: record_note is a no-side-effect example wired through
                // ApprovalRequiredAIFunction (the gating marker) to demonstrate the
                // interactive-approval + durable auto-approval flow. Replace with real gated actions as
                // they are added.
                //
                // create_plan / update_plan_step are the agentic-UI demo tools: the model calls them to
                // build and advance the client's live "Task Progress" checklist. They are UN-GATED — they
                // have no side effects and only drive display state, so interrupting them for approval would
                // break the demo. The data they return IS the UI state (create_plan = a full snapshot,
                // update_plan_step = an incremental JSON Patch delta); AgenticUIAgent surfaces it to the
                // client. jsonOptions carries the serializer context that knows Plan/Step/StepStatus/
                // JsonPatchOperation.
                Tools =
                [
                    new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
                        (string note) => $"Recorded: {note}",
                        "record_note",
                        "Records a short note for the user. Requires approval.")),

                    // Two email tools, both real app-only Microsoft Graph sends (see EmailTools). The recipient
                    // split is what carries the security model.
                    //
                    // send_email(to, subject, body): sends to an ARBITRARY recipient. On the human-only floor
                    // (see humanOnlyPolicy above), so it can never be auto-approved or remembered — it prompts
                    // for a fresh human decision on every send.
                    new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
                        (string to, string subject, string body, CancellationToken cancellationToken)
                            => emailTools.SendEmailAsync(to, subject, body, cancellationToken),
                        "send_email",
                        "Sends an email to any recipient (recipient, subject, body). Requires approval every time.")),

                    // send_self_email(subject, body): sends ONLY to the owner. The recipient is fixed from
                    // configuration (Email:SenderAddress) inside EmailTools, so it is NOT a model-visible
                    // argument — the model supplies only subject and body and the tool is structurally
                    // incapable of emailing anyone else. Auto-approved (see AutoApprovalRules), so it never
                    // prompts; EmailTools records each send to the send log for accountability.
                    new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
                        (string subject, string body, CancellationToken cancellationToken)
                            => emailTools.SendSelfEmailAsync(subject, body, cancellationToken),
                        "send_self_email",
                        "Sends a message to the owner (the user) themselves — a summary, reminder, or notification. Takes no recipient; can only reach the owner.")),
                    AIFunctionFactory.Create(
                        AgenticPlanningTools.CreatePlan,
                        name: "create_plan",
                        description: "Create a plan with multiple steps.", jsonOptions),
                    AIFunctionFactory.Create(
                        AgenticPlanningTools.UpdatePlanStepAsync,
                        name: "update_plan_step",
                        description: "Update a step in the plan with new description or status.", jsonOptions),

                    // show_images: un-gated. The model calls it with image URLs it found (e.g. via
                    // web search) and the client renders a gallery attached to the answer. The returned
                    // object IS the UI content (an "images" array), which the client detects by shape.
                    AIFunctionFactory.Create(
                        ImageTools.ShowImages,
                        name: "show_images",
                        description: "Show one or more images to the user inline, alongside your text answer.",
                        jsonOptions),
                ],

                Reasoning = new() { Effort = ReasoningEffort.Medium },
            },
        });
        // </create_agent>

        // Register as a NAMED hosted agent (returns a hosted-agent builder)
        var hostedAgent = builder.AddAIAgent(name, (_, _) => baseAgent);

        // Durable "one context" store. Replaces the in-memory store so the single context survives
        // restarts. MyAgentSessionStore routes every request to one fixed context (ignoring the client
        // threadId) and persists it as a serialized session snapshot, atomically, under a single-writer
        // guard.
        //
        // Storage is behind a swappable ISnapshotStore, chosen by the same backend flag as the working store
        // above: FileSnapshotStore writes readable JSON into an app-relative context/ dir for local dev,
        // machine-owned and kept OUT of the user-editable working/ store; BlobSnapshotStore persists to
        // Azure Blob (its own container on the shared client) for the cloud end-state. The default is the
        // FILE backend, so a local run with no extra config is unchanged.
        //
        // withIsolation:false is the single-user setting — isolation is the multi-user knob and is a
        // hard requirement for any exposed/multi-user deployment (see SERVER-SECURITY-NOTES.md).
        ISnapshotStore snapshotStore = storage.SnapshotStore;
        var durableSessionStore = new MyAgentSessionStore(snapshotStore);
        hostedAgent.WithSessionStore(durableSessionStore, withIsolation: false);

        // DESIGN TRIP-WIRE — stale-conversation recovery is intentionally NOT composed here. The design
        // once called for AgenticUIAgent( SessionRecoveringAgent( baseAgent ) ), but both wired providers
        // run client-managed history (Azure via store=false — see CreateAzureOpenAIChatClient; the free
        // OpenRouter Responses endpoint is already client-managed), which eliminates the stale-pointer
        // (previous_response_not_found) failure class entirely. So SessionRecoveringAgent is RETIRED on
        // this path, not merely deferred — there is no error for it to recover from. It returns ONLY if a
        // stateful, server-side-history provider (store=true, holding history behind a
        // previous_response_id / conversationId pointer) is ever wired. If that happens, BUILD it
        // (DelegatingAIAgent overriding RunCoreStreamingAsync, advance-first, reusing
        // MyAssistant.DurableContext.SessionRecovery) and compose it here — and note the difficulty a
        // reflection probe found: the stale pointer is ChatClientAgentSession.ConversationId (internal
        // setter, not in StateBag) and the AG-UI host saves its own session by reference, so the repair
        // needs reflection into preview internals to persist cross-turn.
        return new AssistantServer(
            // baseAgent wrapped in StreamErrorLoggingAgent, a debug-level diagnostic that surfaces the real
            // exception behind the AG-UI host's generic streaming error (enable via the Logging config).
            new AgenticUIAgent(
                new StreamErrorLoggingAgent(baseAgent, diagnosticLoggerFactory.CreateLogger<StreamErrorLoggingAgent>()),
                jsonOptions),
            baseAgent,
            new DurableHistoryReader(snapshotStore),
            durableSessionStore,
            approvalStore,
            humanOnlyPolicy);
    }
}
