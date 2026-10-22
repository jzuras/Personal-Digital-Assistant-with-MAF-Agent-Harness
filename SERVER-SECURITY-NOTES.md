# Server security notes — interactive tool approval over AG-UI

**Who should read this:** anyone deploying the MyAssistant AG-UI server (the hosted agent) where tools
require human approval — especially if the agent endpoint may be reachable by clients you don't fully
control.

## Short version

To make **interactive** human-in-the-loop tool approval work over AG-UI on the current preview of
Microsoft Agent Framework (MAF), the server sets:

```csharp
new HarnessAgentOptions { DisableApprovalResponseBinding = true, /* ... */ }
```

This **deliberately turns off a security hardening** the framework added in PR
[#8641](https://github.com/microsoft/agent-framework/pull/8641), as an **interim workaround** (see
*Revert condition* below — removing it will likely require this server to run a proper session store,
and possibly an upstream fix as well; not yet determined). It is a reasonable trade-off for a
**single-user, purpose-built, non-exposed** deployment, but **do not expose this server to untrusted or
generic clients without re-evaluating** (see below).

## Background — how this server works, and why the framework default doesn't fit it

This server hosts the agent as a **remote service** and talks to a **separate client** (a web UI) over
the AG-UI protocol. That client/server split is the whole point: the human is on the client, the agent
runs on the server.

Tool approval in that split is necessarily a **round-trip across the network, in two separate HTTP
requests**:

1. The agent reaches a tool that requires approval, **pauses**, and the server sends an *approval
   request* to the client (one response).
2. The user approves or denies in the UI; the client sends the *decision* back as a **new, later
   request**.

For the paused run to resume on request 2, the server must **persist the run/conversation state after
request 1 and reload it for request 2** (via a session store, keyed by the client's thread). Persisting
and reloading the context between requests is inherent to a hosted, resumable agent — and it's also what
a "one durable context that survives restarts" assistant needs.

Here is the collision. **The framework's default (PR
[#8641](https://github.com/microsoft/agent-framework/pull/8641)) binds the approval decision to
in-memory, framework-owned state it recorded at pause time — and that binding state does not survive the
persist/reload cycle** (a gap in the current MAF preview). So when the decision arrives on request 2, the
reloaded run still has the approval *request* (it's in the saved transcript) but not the framework's
record of it as "pending" — so the decision can't bind, and the turn fails with
`InvalidOperationException: ToolApprovalRequestContent … no matching ToolApprovalResponseContent`.

Why you may not have hit this elsewhere: a **single-process** setup (e.g. a console app with one
long-lived in-memory session) never serializes/reloads that state between the request and the response,
so the default works there. It is specifically the **hosted, split, resumable** shape — what this server
is — that trips it. No upstream fix for this exact case is merged or in-flight (verified Oct 2026; see
issues [#9102](https://github.com/microsoft/agent-framework/issues/9102) and
[#9130](https://github.com/microsoft/agent-framework/issues/9130)).

Disabling the binding makes approval pairing fall back to the message list, which works — at the cost
described below.

## What the flag changes

- **Binding ON (framework default):** an approval decision is accepted only when it binds to approval
  state the framework itself recorded when it paused the run. The server is authoritative about what was
  pending.
- **Binding OFF (this server's setting):** the decision is paired to its request from the message list
  rather than from framework-owned state. The server leans on the (client-involved) resume to pair the
  decision.

## The trade-off / threat

Disabling the binding **weakens the server's authoritative guarantee** that an approval decision
corresponds to exactly the tool call the server recorded as pending. Assume conservatively that an
**untrusted client could attempt to forge, replay, or mis-pair an approval** via the resume path. (This
is the classic "bind the approval to exactly what was shown" problem; the standard guard is to record a
content/arguments hash of the proposed call at propose time and verify it before executing.)

**Scope — this affects only the interactive approval round-trip.** Durable, standing auto-approvals are
**not** affected: they are evaluated entirely server-side from the rule store, with no client
involvement.

## When it's fine vs. when it's not

**Fine as-is:**
- Single-user, local, or a **purpose-built / first-party client** you control.
- The agent endpoint is **not** exposed as an open URL to untrusted clients.
- (This is the typical "assistant with a first-party client" model — the client is yours, not arbitrary.)

**Re-evaluate — do NOT ship as-is:**
- The agent endpoint is reachable by clients or users you don't control.
- Multi-tenant or internet-exposed deployments.

## If you need to expose it to untrusted clients

- **Keep binding ON** and wait for / adopt an upstream fix that makes approval resume survive the AG-UI
  session reload with binding intact; **or**
- Add your **own server-authoritative binding** — e.g. record a content/args hash of the proposed call
  at propose time and verify it before executing, so a tampered or replayed approval is rejected; **and**
- Add **authentication + per-caller session isolation** (`AgentIsolationKeyProvider`) so one caller
  cannot act on another caller's thread.

## Revert condition

Treat this flag as a workaround to remove once the framework's approval binding can survive the
persist/reload this hosted server needs. These are **not two independent alternatives** — a proper
session store is the likely **prerequisite** either way:

- **Prerequisite — a correct session store.** PR #8641 states the binding is meant to survive "when the
  documented configuration is used," and its own review flagged the *sample* store as lacking
  **atomic state consumption** and **principal isolation**. The dev `WithInMemorySessionStore` used here
  lacks both — so **the in-memory store is not expected to be a supported configuration, even if the
  underlying bug is fixed upstream.** A store providing atomic consume + an isolation key is the first
  requirement.
- **Possibly also an upstream fix.** With a correct store in place, the binding *may* survive on the
  current preview — **or** it may still hit the open, reproduced bug
  [#9102](https://github.com/microsoft/agent-framework/issues/9102) and additionally require a MAF fix.

So the route back to the framework default (regaining #8641's protection) is: run a correct session
store, re-enable the binding, and re-test the approval round-trip; if it still fails, an upstream fix is
also needed.

**Status: unverified as of this writing** — whether a correct store alone suffices, or an upstream fix is
also required, is not yet determined. Do **not** assume an upstream fix alone (while still on an
in-memory store) will remove the need for this flag. Until one of these is confirmed, the flag stays and
this trade-off applies.

## References

- Upstream (Microsoft Agent Framework): PR
  [#8641](https://github.com/microsoft/agent-framework/pull/8641) (introduced the approval-response
  binding), and issues [#9102](https://github.com/microsoft/agent-framework/issues/9102) and
  [#9130](https://github.com/microsoft/agent-framework/issues/9130) (the AG-UI resume gaps this works
  around).
