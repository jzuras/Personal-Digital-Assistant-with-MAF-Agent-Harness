# Server security notes — interactive tool approval over AG-UI

**Who should read this:** anyone deploying the MyAssistant AG-UI server (the hosted agent) where tools
require human approval — especially if the agent endpoint may be reachable by clients you don't fully
control.

## Status — RESOLVED 2026-10-24 (binding restored)

> **This workaround has been REMOVED.** Testing confirmed that with a proper durable
> session store in place (`MyAgentSessionStore`: atomic temp-then-rename + single-writer), re-enabling
> the framework's approval-response binding makes interactive approval resume correctly over AG-UI on
> **both** OpenRouter and Azure. `DisableApprovalResponseBinding` is no longer set — binding is back at
> the framework default (**ON**), so **PR #8641's server-authoritative approval guarantee is in force
> again.** The rest of this doc is retained as the historical record of why the workaround existed and
> what it traded off.
>
> **The server is still single-user / not for untrusted exposure — but for a DIFFERENT reason.** The
> binding is no longer the blocker; what keeps it single-user now is that per-caller **session isolation
> is off** (`withIsolation:false`, one shared durable context), plus the concurrency and
> memory-at-scale items in the multi-user section below. Those remain hard requirements before any
> multi-user or internet-exposed deployment.

## Short version (historical — the workaround, now removed)

To make **interactive** human-in-the-loop tool approval work over AG-UI on an earlier preview of
Microsoft Agent Framework (MAF), the server *temporarily* set:

```csharp
new HarnessAgentOptions { DisableApprovalResponseBinding = true, /* ... */ }
```

This **deliberately turned off a security hardening** the framework added in PR
[#8641](https://github.com/microsoft/agent-framework/pull/8641), as an **interim workaround**. As of
2026-10-24 it is **removed** — a correct session store proved to be the missing prerequisite
(see *Revert condition*, now met). It was a reasonable trade-off for a **single-user, purpose-built,
non-exposed** deployment while it was in place.

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

**Status: RESOLVED 2026-10-24.** A correct store alone sufficed: with `MyAgentSessionStore`
(atomic temp-then-rename + single-writer) in place, re-enabling the binding and re-running the approval
round-trip succeeded on both OpenRouter and Azure — no upstream fix needed, #9102 did not reproduce. The
flag has been removed and binding is back at the framework default (ON).

## Durable-context model — additional requirements for multi-user / exposed deployments

The single-user durable-context design deliberately skips
several protections that are **safe to skip for a single, trusted user on a non-exposed server** but become
**hard requirements for any multi-user or internet-exposed deployment**. Anyone building on these ideas for
more than one user must add them:

- **Per-caller session isolation.** The single-user server registers its session store with
  `withIsolation: false`, and routes *every* request to **one** durable context — correct for one user,
  catastrophic for many (every caller would share one conversation + memory). MAF provides the fix as a
  composable wrapper: register with **`withIsolation: true`** so the store is wrapped by
  `IsolationKeyScopedAgentSessionStore` (+ an `AgentIsolationKeyProvider`), and **do not** collapse all
  thread ids to one context. Pair with real authentication so the isolation key maps to an authenticated
  principal. (This expands the "auth + per-caller isolation" bullet above with the concrete MAF knob.)
- **Server-authoritative approval binding.** As of 2026-10-24 this is **ON** — the #8641 binding was
  restored once the durable store existed (see *Status* at the top and *Revert condition*). For
  single-user that fully covers it. For multi-user/exposed, the binding guarantee holds, but you must
  still add **authentication + per-caller isolation** (next bullet) so an approval cannot be mis-paired
  across principals; a content/args-hash check is now optional belt-and-suspenders, not a required
  substitute.
- **Concurrency control on the session store.** The single-user store relies on a **single-writer guard**
  for crash-safety only (one user ⇒ access to the one context is already serial). With multiple concurrent
  callers hitting their own contexts — or worse, racing on a shared one — this becomes a genuine concurrency
  problem: the store needs atomic compare-and-swap / optimistic-concurrency (e.g. ETag/conditional writes on
  a blob backend), not just temp-then-rename.
- **Memory-as-untrusted-input, at scale.** The assistant already treats files/web/memory as data, not
  instructions (a prompt clause). Once memory is shared, imported, or multi-tenant, that discipline becomes
  load-bearing against memory-poisoning (a documented 2026 attack class, incl. the SpAIware ChatGPT-memory
  exploit): validate/scope imported memory, and never let one principal's memory influence another's agent.

These are **flags, not implementations** — none is built, because the target is single-user. They are
recorded so the trade-offs are explicit for anyone taking this design multi-user.

## References

- Upstream (Microsoft Agent Framework): PR
  [#8641](https://github.com/microsoft/agent-framework/pull/8641) (introduced the approval-response
  binding), and issues [#9102](https://github.com/microsoft/agent-framework/issues/9102) and
  [#9130](https://github.com/microsoft/agent-framework/issues/9130) (the AG-UI resume gaps this works
  around).
