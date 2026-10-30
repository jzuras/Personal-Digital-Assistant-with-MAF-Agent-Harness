# Interactive tool-approval over AG-UI, with a server-authoritative client

**Who should read this:** anyone building human-in-the-loop **tool approval** on the Microsoft
Agent Framework (MAF) over the **AG-UI** integration — *and* anyone from Microsoft/ag-ui curious
how the approval path behaves when a consumer deliberately runs the client **server-authoritative**
(server owns conversation history) instead of the default client-owns-history model. Companion to
**`DURABLE-CONTEXT-NOTES.md`** (why this app is server-authoritative in the first place) and
**`SERVER-SECURITY-NOTES.md`** (the approval-binding trade-off and multi-user caveats).

> Preview-software caveat: this is built on `AGUI.*` and preview `Microsoft.Agents.AI.*` packages.
> Names and behavior drift between versions; treat everything here as "true as of the versions in
> *Versions tested* below," not as a stable contract.

---

## TL;DR

- **Goal:** one durable, server-owned conversation context (see `DURABLE-CONTEXT-NOTES.md`). To get
  it, the AG-UI client is made **server-authoritative** — it sends only the *new* user turn each
  request and lets the server replay history — which is the **opposite** of the AG-UI .NET SDK's
  default assumption (client owns the thread and replays full history; server is per-request
  stateless).
- **What broke:** interactive tool **approve/deny** stopped resuming. The decision never reached the
  server as an AG-UI `Resume` entry, so the server's durably-paused approval was never answered
  (`"no approval response was provided for this approval required tool call"`).
- **Root cause:** MAF runs a **client-side** approval pipeline (`ApprovalResponseBindingChatClient`
  → `FunctionInvokingChatClient.ExtractAndRemoveApprovalRequestsAndResponses`) that reconciles an
  approval **request ↔ response by pairing them in the message list**. A server-authoritative client
  doesn't replay the original request, so the lone response is consumed with **no paired request** →
  it is **silently dropped** → `AGUIChatClient` emits an **empty `Resume`**. (Confirmed with a
  fake-transport probe: response-as-message yields `Resume.Count == 0`, with no error.)
- **Fix (clean, sanctioned):** build the `AGUIResume` **ourselves** and hand it to the client via
  `ChatOptions.RawRepresentationFactory` returning a `RunAgentInput { ThreadId, Resume = [...] }`,
  sending **no** approval message. `AGUIChatClient.BuildRunAgentInput` honors a caller-supplied
  `Resume` (its `callerSuppliedResume` path) and forwards it straight to the wire, bypassing the
  client-side pairing pipeline. The payload echoes the tool call, which is exactly what the server's
  AG-UI decode uses to rebuild the approval without replayed history.
- **Verdict:** **not a framework correctness bug on its intended path** — we inverted the model it's
  built around, using a seam the SDK explicitly exposes for exactly this (`RawRepresentationFactory`).
  There is **one genuine rough edge worth Microsoft's attention:** the client-side pipeline drops an
  unpaired approval response **silently** (no diagnostic), whereas the analogous server-side path
  *does* emit one. This sits next to a known, acknowledged open issue in the same area (**#9102**).

---

## The goal and the inversion

A personal assistant wants **one durable context that the server owns** — it survives restarts and
is the same context for any client that connects (full rationale in `DURABLE-CONTEXT-NOTES.md`). On
MAF that means a server-side `AgentSessionStore` plus a client that sends **only the new turn** and
lets the server supply history.

The AG-UI .NET SDK is built for the **opposite** arrangement, and says so in its own code:

- `AGUIChatClient` deliberately **clears `ConversationId`** so the MEAI pipeline sends the **full
  message history every turn** — otherwise history would be truncated against a *stateless* AG-UI
  server (ag-ui issue **#4869**).
- The server's resume decoder (`AGUI.Server`'s `RunAgentInput.ToChatRequestContext`) recovers the
  tool call a resume answers "from the run's messages (**which a resuming run carries by spec**)," with
  an *echo* fallback (a `toolCall` embedded in the resume payload).

So "server owns history" is swimming upstream of the integration's central assumption. That is a
deliberate, legitimate choice on our side — but it means the **resume path is ours to get right**.

## What broke, precisely

With the server-authoritative (delta-only) client, interactive approval failed reproducibly: the
run paused correctly, the user clicked **Approve**, a second HTTP request went out, the server
resumed the correct paused run — and then **rejected** the tool call with *"no approval response was
provided."* The decision never arrived as a `Resume`.

MAF (on both client and server, with PR **#8641**'s approval binding **ON**) runs an approval
pipeline that, on the **client** side, sits above `AGUIChatClient`:

```
ChatClientAgent
  → ApprovalResponseBindingChatClient
    → ApprovalNotRequiredFunctionBypassingChatClient
      → AGUIChatClient
        → FunctionInvokingChatClient.ExtractAndRemoveApprovalRequestsAndResponses
```

`ExtractAndRemoveApprovalRequestsAndResponses` reconciles a `ToolApprovalRequestContent` with its
`ToolApprovalResponseContent` **by matching them in the message list**. In the client-authoritative
model that pairing is always satisfied, because the client replays the thread (the request is right
there). In our model the thread carries **only the new turn**, so:

- A decision sent **as a `ToolApprovalResponseContent` message** has **no paired request** → it is
  **silently dropped** → `AGUIChatClient` builds an **empty `Resume`** → the server's
  durably-restored pending approval is never answered.
- (Sending the request **and** response together instead throws client-side:
  `"ToolApprovalRequestContent ... has no matching ToolApprovalResponseContent"`.)

**Precision note (confirmed vs. inferred).** The request+response-pair **throw** is confirmed by
stack trace to originate in `FunctionInvokingChatClient.ExtractAndRemoveApprovalRequestsAndResponses`.
For the response-only case we confirmed the **effect** — an empty `Resume` on the wire, with no
exception and no diagnostic — but did **not** instrument exactly which layer of the stacked
client-side pipeline discards the response; it happens somewhere upstream of `AGUIChatClient`'s
message-to-`Resume` translation. The system-observable behavior (the decision is lost, silently) is
the same regardless of the precise internal discard point.

### How this was confirmed

A throwaway probe implemented a fake `IAGUITransport` and captured the exact `RunAgentInput` the
client produced:

| Client sends… | `RunAgentInput.Resume` on the wire |
|---|---|
| `ToolApprovalResponseContent` as a message (server-authoritative) | **0** (silently dropped) |
| same, but with no run options at all (the earlier interim) | **0** (identical — rules out our options wiring) |
| request + response as a paired message | **client-side throw** in `ExtractAndRemoveApprovalRequestsAndResponses` |
| **caller-built `AGUIResume` via `RawRepresentationFactory`, no approval message** | **1**, correct `InterruptId` + echoed `toolCall` |

The empty-`Resume` cases produced **no exception and no diagnostic** — the failure only surfaced
later, server-side, as *"no approval response was provided."*

## The fix

In the approval-response handler, build the AG-UI resume entry directly and drive the wire format
through the SDK's sanctioned seam:

```csharp
var toolCall = request.ToolCall as FunctionCallContent;

var payload = JsonSerializer.SerializeToElement(new AGUIToolApprovalResumePayload
{
    Approved = approved,                       // approve vs. deny
    ToolCall = toolCall is null ? null : new AGUIToolCallInfo
    {
        CallId = toolCall.CallId,
        Name = toolCall.Name,
        Arguments = toolCall.Arguments,        // echo the call so the server can rebuild the approval
    },
});

var resume = new List<AGUIResume>
{
    new() { InterruptId = request.RequestId, Status = ResumeStatus.Resolved, Payload = payload },
};

// Hand the Resume to AGUIChatClient via RawRepresentationFactory, with NO approval message.
var options = new ChatClientAgentRunOptions(new ChatOptions
{
    RawRepresentationFactory = _ => new RunAgentInput { ThreadId = stableThreadId, Resume = resume },
});
await foreach (var _ in agent.RunStreamingAsync(Array.Empty<ChatMessage>(), session: null, options)) { }
```

Why this is the *right* seam, not a hack: `AGUIChatClient`'s internal-keys file explicitly directs
callers here — *"callers should drive the wire format through `ChatOptions.RawRepresentationFactory`
(returning a `RunAgentInput`)."* `BuildRunAgentInput` treats a caller-supplied `Resume` as
authoritative and forwards it untouched, so the client-side pairing pipeline is never engaged. The
echoed `toolCall` is the input the server's decoder uses to reconstruct the approval **without**
the replayed history we no longer send.

Two adjacent details that are easy to trip on:

- **Thread id, not `ConversationId`.** The stable thread id is carried on the same `RunAgentInput`,
  not via `ChatOptions.ConversationId`. A non-null `ConversationId` puts `ChatClientAgent` in
  *service-managed* mode, which expects the inner client to **echo a `ConversationId` back** on each
  response — but `AGUIChatClient` nulls it (ag-ui **#4869**), so that path throws *"Service did not
  return a valid conversation id."*
- **Binding stays ON.** This does **not** require `DisableApprovalResponseBinding`; PR #8641's
  server-authoritative binding is retained. The durable server session holds the pending approval;
  the caller-built `Resume` answers it by `InterruptId`.

## Bug, or using it outside its design?

Broken out honestly, because the answer differs by piece:

1. **The inversion itself (server-authoritative over an SDK built for client-authoritative):** *not a
   bug* — intended-usage boundary. And it is **supported**: `WithSessionStore` is a first-class MAF
   feature, and `RawRepresentationFactory → RunAgentInput` is the documented "drive the wire yourself"
   seam. This is advanced-but-sanctioned use, not abuse.
2. **The server decoder requiring the pending call in the run's messages (or an echoed `toolCall`):**
   *not a bug* — it is the documented contract for a stateless-server resume, and the echo fallback
   is exactly what lets a server-authoritative client resume without replaying history.
3. **The client-side pipeline silently dropping an unpaired approval response:** *the one genuine
   rough edge.* In the client-authoritative model it was built for, dropping an "impossible" unpaired
   response is defensible. But doing it **silently** is inconsistent with the SDK's own standards:
   the *server*-side resume path, when it drops responses (its `callerSuppliedResume` override),
   deliberately emits an `ActivityEvent`
   (`agui.resume.caller_override_dropped_responses`) "to make the drop observable instead of silent."
   The client-side drop has no equivalent, so a lost approval decision becomes a confusing
   *downstream* error far from its cause.

### A concrete, minimal suggestion (if anyone from MS reads this)

Emit a diagnostic (an `ActivityEvent`, or at least a debug log) when the client-side approval
pipeline discards a `ToolApprovalResponseContent` that has no matching request in the message list —
mirroring the observability the server side already provides. That one change would have collapsed
this investigation from hours to minutes, and costs nothing on the happy path.

### Context: this area is known-rough upstream

This is **not** purely a consequence of our unusual usage. The exact combination of **AG-UI +
`AgentSessionStore` + approval resume** already has an open, maintainer-labeled-`reproduced` issue
(**#9102**, a duplicate-request "same key" collision), and the same surface was contested earlier via
PR **#8641**'s binding change. The framework's story for *durable, server-held tool approval over
AG-UI* is genuinely immature, not merely unfamiliar — our scenario is adjacent terrain.

### The honest framing

> We deliberately inverted the AG-UI history model to get **one durable context**, and the approval
> path is where that inversion has a cost — which the framework lets you pay cleanly through
> `RawRepresentationFactory`, modulo one silent-drop rough edge.

## A residual we have *not* exercised

With the fix in place the server holds the pending approval from its durable session **and**
reconstructs one from the incoming `Resume`. That is the shape of issue **#9102**'s "same key"
collision. It did **not** fire in our testing (approve executes cleanly), but it is the thing to
watch under heavier or concurrent use.

## Versions tested

Analysis and the fake-transport probe were run against **`AGUI.Client` 1.0.1** and
**`Microsoft.Agents.AI` 1.26.0** (pulling **`Microsoft.Extensions.AI` 10.11.0**), reading the
`ag-ui` dotnet SDK and `microsoft/agent-framework` source at matching versions. `MapAGUIServer`
(from `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore`) delegates resume decoding to `AGUI.Server`'s
`RunAgentInput.ToChatRequestContext`, which is the decoder referenced above.

## References

- `DURABLE-CONTEXT-NOTES.md` — why this app is server-authoritative (the durable-context build).
- `SERVER-SECURITY-NOTES.md` — the approval-binding trade-off (now resolved) + multi-user requirements.
- Upstream (microsoft/agent-framework and ag-ui): PR **#8641** (approval-response binding), issue
  **#9102** (AG-UI + session store + approval, "same key"), ag-ui **#4869** (stateless server /
  history-truncation, why `ConversationId` is cleared), and **#7395 / PR #8838** (a separate
  compaction defect discussed in `DURABLE-CONTEXT-NOTES.md`). Verify status against the live trackers.
