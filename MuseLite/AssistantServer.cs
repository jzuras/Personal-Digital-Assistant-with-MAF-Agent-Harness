using Microsoft.Agents.AI;
using MuseLite.AgenticUI;
using MyAssistant.ApprovalPolicy;
using MyAssistant.DurableContext;

namespace MuseLite;

/// <summary>
/// Bundles what Main needs after the server composition builds the agent: the AG-UI agent to map, the
/// base agent (for the reset endpoint's session delete) and durable session store, the shared history
/// reader the /history endpoint delegates to, and the approval store + human-only policy for the
/// approvals endpoint.
/// </summary>
/// <param name="Agent">The AG-UI agent mounted at <c>/invocations</c>.</param>
/// <param name="BaseAgent">The base agent, used by the reset endpoint's session delete.</param>
/// <param name="HistoryReader">Reads the durable snapshot for the <c>/history</c> endpoint.</param>
/// <param name="SessionStore">The durable one-context session store.</param>
/// <param name="ApprovalStore">The durable approval-rule store for the approvals endpoint.</param>
/// <param name="HumanOnlyPolicy">The human-only floor, re-checked server-side on approval persist.</param>
internal sealed record AssistantServer(
    AgenticUIAgent Agent,
    AIAgent BaseAgent,
    DurableHistoryReader HistoryReader,
    MyAgentSessionStore SessionStore,
    ApprovalRuleStore ApprovalStore,
    HumanOnlyPolicy HumanOnlyPolicy);
