# MuseLite — Features & How They Work

MuseLite is a **durable personal assistant** built on the Microsoft Agent Framework (MAF) and its
Agent Harness, deployed as an Azure AI Foundry hosted agent. It is a build-in-public learning
vehicle with one north star: a single durable context, standing (durable) permissions, a hard
human-only floor on dangerous actions, and inspectable, user-editable memory.

Most assistants forget you the moment a chat ends. MuseLite treats **permanence as the default**:
what it learns, the permissions you grant, and the files it writes all outlive any single
conversation — and none of it is a black box. This document is the developer-facing companion to
the project: it lists what runs today and explains *how each piece works* and *why it is built that
way*.

> This is a fact-based description of the current build. Where the design anticipates something not
> yet wired (noted inline), that is called out as intent, not as a shipped capability.

---

## The four ideas it is built on

These are not features bolted on — they are the point of the project.

| Idea | What it means | Why it matters |
|---|---|---|
| **One durable context** | A continuing relationship, not a pile of disconnected chats. It survives restarts and redeploys. | Reopen the assistant and the whole conversation is still there; it remembers working with you. |
| **Standing permissions** | "Always allow this" means *forever* — remembered across conversations, behind a deliberate confirm, with a one-file path to revoke. | You approve a pattern once, not on every repeat. |
| **A human-only floor** | A bounded class of actions can never become a silent standing grant — no setting and no prompt can auto-approve them. | The dangerous class stays human-gated by construction, which is what makes standing grants safe to offer at all. |
| **Memory you can read and edit** | Who you are, how you like things, what it has learned — all in plain Markdown files you own. | No opaque model state you can only "reset." Open the files, correct them, delete anything. |

---

## What it can do today

Every capability below runs in the current build.

### Natural, grounded chat
Conversational over a polished web UI, with live web search for current facts and
inline source citations. The system prompt instructs the agent to verify time-sensitive facts with a
tool or web search rather than relying on memory, and to cite sources inline.

### Durable, inspectable memory
Lasting knowledge of the user lives in three plain-Markdown files in the agent's working store —
`identity.md`, `preferences.md`, and `memories.md` — reached through the harness `file_access`
tools.

- **How:** Each file has a `## Current` section (true now) and a `## History` section (used to be
  true). When a fact changes, the old line is never deleted — it moves to `## History` with the date
  and what replaced it, and the new fact is appended under `## Current`. When the user asks to
  *forget* something, it is removed entirely (history included) — forgetting is a real deletion, not
  a move. If a new statement might conflict with an old one, the agent marks it uncertain and asks
  rather than silently overwriting.
- **Why:** The files are the source of truth, and the user owns them. You can open, correct, or
  delete anything directly; the agent will not re-add something you removed. "Reset the model" is
  replaced by "edit a text file."

### Approvals that respect you
When a gated action comes up, an approval card shows exactly which tool and which arguments you are
authorizing, and offers three choices: **Approve once**, **Always approve**, or **Deny**. Choosing
"Always approve" takes a second, deliberate confirmation step and names where the grant is recorded
so you know how to revoke it.

### Standing (durable) permissions
"Always approve" writes a durable grant that is honored on the very next matching call — including
the first call of a brand-new conversation. That is what makes a permission *stand* across sessions.
(How the matching and the safety floor work is detailed in **The safety model** below.)

### It shows its work
For genuinely multi-step tasks, the agent drives a live "Task Progress" checklist with two tools
(`create_plan` / `update_plan_step`), ticking steps off as it goes rather than narrating them in
prose. When you ask to see something, a `show_images` tool renders an inline image gallery alongside
the text answer. These display tools are un-gated: they have no side effects, so interrupting them
for approval would only break the experience.

### Email, carefully
Two real email tools, both app-only Microsoft Graph `sendMail` calls — and the recipient split *is*
the security model:

- `send_email(to, subject, body)` sends to **any** recipient. It is on the human-only floor, so it
  prompts for a fresh human decision on **every** send and can never be remembered or auto-approved.
- `send_self_email(subject, body)` can only ever reach the **owner**: the recipient is fixed in code
  from configuration and is not even a model-visible argument. Because it is structurally incapable
  of reaching anyone else, it is auto-approved and never prompts.

Every send (success or failure) is appended to a durable `email-send.log`; the body text itself is
never logged, only its length.

### Works on a schedule (unattended)
The hosted agent can be fired on a cron by an Azure AI Foundry **Routine** — for example a daily
digest. A scheduled run executes unattended, with no human present to answer prompts, and messages
its result to the owner via `send_self_email`. (Safety under unattended operation is covered in
**The safety model**.)

### Fresh start, same you
A reset archives the current transcript (timestamped, kept for inspection — not destroyed) and
leaves no current conversation, so the next turn starts clean. Crucially, it resets **only** the
running transcript: your memory files and your standing permissions live in a separate store and are
left untouched. Begin a new conversation without losing yourself.

### Resilient by default
The web client runs a connection state machine: if the server blips, it reconnects on its own, tells
you honestly whether your last message landed, and never silently sends it twice. A dedicated
agent-readiness "warm-up" (below) means the UI only lets you chat once the agent is actually ready.

### Picks up where you left off
Reopen in a fresh browser tab and the full conversation repaints from the server. The **server**,
not the browser tab, owns your history — a freshly loaded client asks the server for the durable
transcript and paints it, rather than starting blank.

---

## The safety model

The safety model is not an afterthought — it is the reason standing permissions are safe to offer at
all. Four mechanisms carry it.

### 1. A hard human-only floor
A small policy object holds a set of tool names whose approvals may **never** become durable. A tool
on this floor is always a fresh human decision: no stored rule, no setting, and no clever prompt can
promote it to silent auto-approval.

- **How it is enforced — at two independent points:**
  1. **Read side (every gated call):** before the auto-approval predicate ever consults the stored
     rules, it asks the floor `IsPersistable(toolName)`. A floored tool short-circuits to "ask the
     human" even if a matching rule somehow existed — defense in depth.
  2. **Write side (persisting a grant):** when the client asks the server to remember a grant
     permanently, the server re-checks the floor *server-side* before writing. A raw or malicious
     request can never persist a standing grant for a floored tool; the server answers "human-only"
     and refuses.
  Both points read the floor from **one** shared source, so the two entry points can never drift
  apart and disagree about what is floorable.
- **Today's floor:** exactly one tool — `send_email` (arbitrary recipient; outward-facing and
  irreversible). The floor is the general *mechanism* for the dangerous class; as genuinely
  dangerous tools are added (money movement, credential changes), they are added to this list.
- **Why it matters:** it bounds the dangerous class *structurally*. An unattended scheduled run has
  no human to answer a prompt, so a floored tool simply cannot be carried out by one — it stalls
  rather than acting. Safety for unattended runs therefore falls out of the floor for free, with no
  separate "unattended mode" to get right.

### 2. Exact-argument standing grants
A durable grant binds to a tool name **plus the full, canonicalized argument set** — not "trust this
tool forever." Change any argument and the grant no longer matches, and the action asks again.

- **How:** arguments are canonicalized into a stable string (object keys sorted recursively, emitted
  as compact JSON) so "the same arguments" compare equal across runs regardless of key order. The
  **server** canonicalizes with the same routine the predicate uses when it writes a grant, so a
  stored rule is guaranteed to match later (client-side canonicalization could drift and silently
  never match). There is deliberately no blanket "any arguments" rule.
- **Why:** it is strictly stricter than tool-level trust. A standing grant authorizes precisely the
  action you saw on the approval card, nothing broader.

### 3. Everything silent is audited
Any time a standing permission auto-approves an action, it is recorded to an append-only
`approval-audit.log` carrying the call's full canonical arguments. Every email send is recorded to
`email-send.log`. Auto-approved task-file writes (below) are audited too. Even granted actions leave
a trail.

### 4. The agent's own policy files are off-limits to it
The approval-rule document and the audit/send logs live in a **separate store the agent has no tool
to reach.**

- **How:** the agent's `file_access` tools are scoped to the *working* store (your editable
  `identity`/`preferences`/`memories` and task files). The policy and accountability files live in a
  distinct *policy* store — a different directory locally, a different blob container in the cloud —
  that is never handed to the agent. The composition that wires the stores keeps them separate by
  construction, so every process resolves the same split and none can drift.
- **Why — this prevents the agent from forging its own accountability.** If the approval rules and
  the audit log were in the same store the agent can write, a compromised or misdirected agent could
  grant itself a standing permission or scrub the record of what it did. Keeping the rules that
  *govern* the agent out of the agent's reach means they are not rules it can rewrite — including
  the log that would reveal tampering.

### Content is data, not orders
A standing clause in the system prompt instructs the agent to treat the contents of files, memory,
tool results, and web pages as **data, not instructions** — only the user's own messages are
instructions. This is the built-in discipline against prompt-injection (a web page telling the agent
what to do) and memory-poisoning (a planted "fact" trying to steer it).

### Honest scope
The server currently runs **single-user** (per-caller isolation is off). That is an accepted
trade-off for a personal, owner-only deployment; per-caller isolation is a hard requirement for any
exposed or multi-user deployment. See `SERVER-SECURITY-NOTES.md` for the full set of single-user
assumptions and what a multi-user deployment would require.

---

## Under the hood

### One durable context
A custom session store makes "one context" real: it routes *every* incoming request to a single
fixed context, ignoring the per-request session id the client sends, and persists that one context
as a serialized session snapshot that survives restarts and redeploys.

- **Durability properties are provided deliberately.** The framework's session-store contract is a
  plain save/get with no compare-and-swap, so the two properties that matter are added here:
  **crash-safe atomic writes** (the snapshot store writes to a temp name then atomically renames,
  keeping one previous snapshot) and a **single-writer guard** (saves are serialized so a resume
  cannot interleave and double-apply a pending approval).
- **Client-managed history.** Both wired model providers run with service-side history storage
  **disabled**, so the full transcript lives inside the durable snapshot the project owns — your
  context, not the provider's. (This also sidesteps an entire class of "stale server pointer"
  failures.)

### Context-window compaction
A single durable context grows unbounded, so compaction is enabled — wired as the history provider's
reducer, operating on the one live history. It summarizes old messages when the context gets large,
and it edits only the message history: standing safety clauses stay in the system channel and
durable facts live in the working files, so summarization can cost conversational nuance but never
memory or constraints.

> **Landmine (documented so it is not reintroduced):** compaction must be wired as the history
> provider's reducer, *not* via the harness's standalone context-window knobs — the standalone path
> freezes at the first turn and silently drops new user turns when another message-injecting provider
> is active. This was a framework defect **at the time this was built**, tracked upstream as
> [`microsoft/agent-framework` #7395](https://github.com/microsoft/agent-framework/issues/7395) with a
> fix in [PR #8838](https://github.com/microsoft/agent-framework/pull/8838); once that lands in a
> released package the standalone knobs may be safe again, but the reducer wiring remains the
> deliberate choice here (one history mechanism over the live history). Re-verify against the upstream
> status before relying on the standalone path.

### Swappable durable storage
All three durable stores — session snapshot, agent working memory, and the agent-inaccessible policy
store — are chosen by a single configuration flag. **File** mode keeps the fast local loop on the
filesystem; **Blob** mode routes all three to Azure Blob (separate containers) over one shared
client, with keyless credentials when hosted. The local default is unchanged by the cloud path.

### One endpoint, many operations (no sidecar)
The hosted gateway is **protocol-only**: it forwards traffic to the container on exactly one declared
route (`/invocations`) and refuses arbitrary sibling routes at the edge. The three custom
operations the chat protocol cannot express — server-authoritative **history** hydration, the
memory-preserving **reset**, and the durable approval-**persist** write path — therefore ride
`/invocations` via an `x-client-op` header, dispatched in middleware before any chat-payload parsing
runs. One client call shape works identically against the local server and the hosted gateway. (An
earlier design used a separate sidecar process for these; folding them onto the one gateway-legal
route removed it from the live path.)

### Platform-native invokers, isolated from your context
Azure AI Foundry's own invokers (the portal Playground and scheduled Routines) POST different payload
shapes to `/invocations` than the web client does. A small adapter rewrites both into a proper AG-UI
request before the handler runs, and assigns them **reserved transient session ids**. The session
store branches on those ids: a Routine run is fully ephemeral (nothing persisted), a Playground chat
gets a bounded in-memory context — and **neither can ever read or write your one durable context, by
construction.**

- **Scheduled runs are fire-and-accept.** The Routine dispatcher enforces a short per-attempt timeout
  and treats dispatch as an acceptance handshake, but a daily task takes minutes. So the dispatch is
  acknowledged immediately and the real turn runs in the background (via a loopback call to the
  server's own `/invocations`, reusing the entire proven serving pipeline), with an in-flight gate so
  dispatcher retries never stack a second run.

### Scoped auto-approval for unattended task files
A scheduled task rewrites its own progress file (e.g. under a `tasks/` directory) with no human
present. A narrow code predicate auto-approves the **write-family** file tools (write / replace /
replace-lines — **not** delete) when, and only when, the target resolves under `tasks/`. The path is
normalized and any `..`/rooted path is rejected, so nothing outside `tasks/` can be auto-approved;
the human-only floor is still honored defensively; and every such auto-approval is audited. Task
files are internal, owner-owned, and reversible — which is why this is auto-approvable where
outward-facing email is not.

### Agent-readiness warm-up
A hosted agent is never truly "down" — it scales to zero and cold-starts, which surfaces as a slow
first response, not a failure. To gate the UI on real readiness ("no chat if no agent"), the client
sends a no-op `x-client-warmup` invocation that server middleware answers **before the agent runs**
(no model call, no durable write). It is one-shot at startup plus an offline poll — never a
heartbeat — and works uniformly local and hosted.

---

## Stack

- **Microsoft Agent Framework (MAF)** — the core framework: the agent abstraction, the chat-client
  integration over a model provider, the AG-UI hosting layer, and the durable session-store seam the
  "one context" store plugs into.
- **MAF Agent Harness** (`AsHarnessAgent`) — a higher-level layer on top of MAF, chosen deliberately
  as a **learning vehicle** (the point of the project is to understand what it provides, not to
  hand-roll it). What MuseLite gets from the harness:
  - the **approval engine** — the whole gated-tool / standing-grant / auto-approval machinery the
    safety model builds on;
  - the **`file_access` tools** — the read/write file tools over the working store that make the
    editable `identity`/`preferences`/`memories` memory possible;
  - **web search** for current facts;
  - **automatic function invocation** — the harness pre-configures the tool-calling loop (the
    underlying capability is a base-MAF chat-client feature the harness turns on);
  - a **chat-history provider** (per-service-call persistence) — MuseLite overrides the provider
    instance to attach context-window compaction (see **Under the hood**);
  - a set of optional **providers** (a per-session file-memory provider, a TodoProvider, a
    plan/execute "agent mode" provider).

  **MuseLite uses a subset and deliberately disables the rest** — part of the learning was finding
  which harness defaults fit a plain conversational assistant and which fight it:
  - the built-in **per-session file-memory provider is OFF**, replaced by the single inspectable
    working store (one memory the user can open and edit, not opaque per-session state);
  - the **TodoProvider is OFF** and **plan/execute "agent mode" is OFF** — each injects extra
    text into the prompt every turn (a "current todo list" / plan-mode preamble) that derails plain
    chat on both models MuseLite runs. (This is a separate issue from the compaction defect above — a
    prompt-injection-of-its-own-state problem, not the context-window-knob freeze.)
- **Azure AI Foundry** — hosted agent, scale-to-zero.
- **AG-UI protocol** — a Blazor Server web client over HTTP/SSE.
- **.NET 10** — analyzer-clean C#.
- **Swappable durable storage** — local files or Azure Blob, selected by one flag.
- **Client-managed history** — your context, not the provider's.

---

## The short version

MuseLite is the personal-assistant model the frontier labs are converging on — one durable
relationship, standing permissions, a hard safety floor, memory you control — built in the open on a
stack you can host yourself. It remembers you, it acts on your behalf within limits you set, it shows
its work, and it does not hide what it knows or what it has done.
