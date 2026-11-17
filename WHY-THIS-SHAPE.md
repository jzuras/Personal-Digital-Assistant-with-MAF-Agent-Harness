# Why this assistant is shaped the way it is (and why it keeps fighting the framework)

**Who should read this:** anyone following the build-series who hits a choice — "why is the server
authoritative?", "why fight AG-UI?", "why hand-roll durable permissions?" — and wants the reasoning
traced all the way back to the goal. This is the **map**; the other notes are the territory. Start
here, then follow the links down.

> Preview-software caveat: built on `AGUI.*` and preview `Microsoft.Agents.AI.*` packages. Specifics
> drift between versions; treat them as "true as of the versions in the companion notes."

---

## TL;DR — the one-sentence thesis

**The Microsoft Agent Framework (MAF) harness and the AG-UI integration are *session-shaped*; a
Muse-style personal assistant is *relationship-shaped* — and almost every "fight" in this project
is the same collision: a thing that must survive *across* conversations meeting a framework default
that assumes everything lives *within* one.**

Two fights dominate — **durable context** and **durable permissions** — and the third battle is
where the two **collide**, over AG-UI. A fourth followed when the finished shape met its hosting
platform: the Azure AI Foundry hosted-agent gateway is protocol-only, and the relationship-shaped
operations that live *beside* the chat protocol had nowhere to land.

---

## The north star

The goal is a **Muse-style personal assistant** (Meta's Muse) built on **MAF + its Agent Harness**,
exposed over **AG-UI**. "Muse-style" means one assistant with a *continuing relationship* with you,
which rests on four pillars:

1. **One durable context** — it remembers working with you across restarts and days, not a pile of
   disconnected chats.
2. **Standing (durable) permissions** — "always allow this" means *forever*, not "until this
   session ends."
3. **A hard human-only floor** — some actions can never become a standing allow, no matter what you
   click.
4. **Inspectable, user-editable memory** — your facts live in plain files you can read and correct.

The friction starts immediately, because MAF uses two of those words — **"durable"** and
**"standing"** — to mean *session-scoped* (its "standing approvals" die with the session; its file
memory's durability leans on session export/import). The whole project is, in a sense, making those
words live up to their names. (Detail: `what-i-changed-in-the-sample.md`.)

---

## The shape, as a map

```
Muse-style assistant on MAF + Harness + AG-UI      (north star: ONE continuing relationship)
        │
        ├── pillar: durable CONTEXT ───────────────────────────────┐
        │     • survive restarts AND be OURS (not the provider's)   │
        │     • one long-lived context must be kept bounded         │
        │        └─ ⚔ FIGHT 1: the history store / compaction       │
        │     • so the SERVER owns history (client sends deltas)     │
        │                                                           ▼
        │                                        ⚔ FIGHT 3:
        │                                         interactive approval RESUME over AG-UI
        │                                         — the point where the two pillars collide
        │                                                           ▲
        └── pillar: durable PERMISSIONS ────────────────────────────┘
              • "always" must mean forever, across sessions
                 └─ ⚔ FIGHT 2: making standing permissions genuinely durable
              • and the grant/deny must round-trip to a UI over AG-UI

   deployment: the whole shape, hosted on Azure AI Foundry Agent Service
        └─ ⚔ FIGHT 4 (this series' latest): the hosted-agent gateway is protocol-only —
           the operations beside the chat protocol (history / reset / standing grants)
           had to reach durable state another way
              └─ round two: the platform's OWN invokers (Playground, Routines) speak
                 Foundry's dialect on that one route — not yours

   quieter pillars: human-only floor (the semantics-blind trade-off) · inspectable memory (the working/ store)
```

Each branch is its own little "why" ladder; the fights hang at the rung where a pillar's choice met
a framework default; and **the two big pillars reconverge at the latest battle** — which is why
*both* durable context and durable permissions were load-bearing in it.

---

## Fight 1 — the history store (durable context, kept bounded)

**Why we're here:** one context that lives forever grows without bound, so it needs **context-window
compaction** (summarize/trim old turns). **The collision:** MAF's convenient knob for that
(`MaxContextWindowTokens`/`MaxOutputTokens`) spins up compaction as a **separate, standalone pass**
that — under our setup, where another component re-injects a message each turn — **froze at the first
turn** and fed the model a stale snapshot, silently dropping new user turns. **The resolution:** wire
the *same* compaction strategy as the history provider's **reducer**, so there is exactly **one**
mechanism on the live history instead of two fighting over the same job. It's a genuine framework
defect, tracked upstream (**microsoft/agent-framework #7395**, fix in **PR #8838**).

A related sub-choice under this pillar: run **client-managed history (`store=false`)** so the context
is genuinely *ours* (a self-contained snapshot on our storage) rather than leaning on the model
provider's server-side retention — which also retired a whole class of "the provider forgot my
conversation" failures.

→ Deep dive: **`DURABLE-CONTEXT-NOTES.md`**.

## Fight 2 — durable permissions (making "standing" mean forever)

**Why we're here:** a personal assistant's standing permissions must survive across conversations;
the harness's "standing approvals" are session-scoped. **The resolution:** a standalone,
dependency-free policy library plus one generic predicate that consults a small JSON grant file on
*every* gated call (including a brand-new session's first call), a non-overridable **human-only
floor**, an audit log, a deliberate second confirm to make a grant permanent, and a safety cap on
auto-approval iterations.

**The honest trade-off:** ours is *semantics-blind*. Muse can say "allow emails to this one
recipient, forever" because it knows the meaningful scope dimension per action; we have no such
taxonomy, so we match on **exact arguments** — strictly safer, but it re-asks whenever anything
changes. A deliberate first-attempt simplification, not the finish line. **Still unfinished:** the
Muse-style *semantic scope-keys*. (The client-side "remember permanently" *write* path has since
been built: the UI's "Always approve" — behind a deliberate second confirm — posts the grant to a
server-side endpoint that re-checks the human-only floor and canonicalizes the arguments with the
same canonicalizer the auto-approval predicate uses, so a stored rule always matches.)

→ Deep dive: **`what-i-changed-in-the-sample.md`**.

## Fight 3 — where they collide: interactive approval resume over AG-UI

This battle belongs to **both** pillars at once. Durable context forced the
**server-authoritative inversion** (the client sends only the new turn; the server owns history —
the opposite of AG-UI's client-owns-history default). That inversion then **starved the permissions
machinery**: MAF's client-side approval pipeline reconciles an approval request and response by
pairing them *in the message list*, and a server-authoritative client no longer replays the request —
so the user's decision was silently dropped and the paused action never resumed. **The resolution:**
have the client build the AG-UI *resume* itself and hand it over a documented seam
(`RawRepresentationFactory`), bypassing the pairing pipeline. The approval binding stays on; approve
executes, deny blocks.

→ Deep dive: **`AGUI-APPROVAL-NOTES.md`** (what broke, the fix, and an honest "bug vs.
using-it-outside-its-design" read).

## Fight 4 — the hosted gateway is protocol-only (this series' latest)

**Why we're here:** the target runtime was **Azure AI Foundry Agent Service** as a *hosted agent*
(bring your own container; Microsoft operates the infrastructure, with scale-to-zero) — learning
that runtime was one of this project's stated goals. The agent deployed and chatted end-to-end
that way. But a relationship-shaped assistant has operations that live *beside*
the chat protocol: hydrating the stored conversation into a freshly opened client, a
memory-preserving "new conversation" reset, and persisting a standing approval. Locally these were
plain REST routes co-located on the agent server, and they worked.

**The collision:** Foundry's hosted-agent gateway forwards traffic to the container **only on the
declared protocol endpoints** (plus a readiness probe). Arbitrary custom routes are refused at the
platform's edge proxy — they never reach the app. Confirmed both empirically (a well-formed request
to a custom path got a 404 from the edge, not from our server) and in Microsoft's hosted-agent
runtime contract documentation. This is by design, not a gap: the platform assumes an agent *is*
its chat protocol. Ours isn't — the same session-shaped-vs-relationship-shaped mismatch, one layer
up.

**The resolution:** notice that two of the three operations are **storage administration, not
conversation** — and the third (history) is a read. So they moved out of the chat path entirely,
into a small **companion app** that reads and writes the *same durable storage* the agent uses,
directly. Chat stays on the protocol path; the side-channel operations bypass the gateway rather
than tunneling through it. This was only possible because of an earlier choice made for different
reasons: durable state lives in **our own Azure Blob storage account** (a pattern Microsoft
explicitly supports), not Foundry's native agent-state store — which only the hosted agent itself
can reach, and which would have closed this exit.

**The honest part:** the companion needs a generic web host anyway — an endpoint, an identity,
storage access. And a generic host is exactly what the *agent* could have used from the start, in
which case the custom routes would simply co-locate and the companion would not exist. So this
fight ends not with a victory but with an open question — keep the Foundry hosted agent plus a
companion, or move the agent to a generic scale-to-zero host (Azure Container Apps is the natural
candidate) where everything co-locates again — and that is where the series currently pauses. One
new Foundry feature weighs on the answer: **Routines** can now schedule the hosted agent (a
cron-style trigger that sends it a prompt), which gives the unattended daily run a native trigger
that a generic host would have to rebuild.

**Round two — the platform's own invokers.** Routines were a reason to stay; turning them on
revealed the next layer. The gateway forwards only the declared protocol route — and it turns out
the platform's *own tools* speak **Foundry's dialect** of that route, not yours: the portal
Playground delivers the typed message as a raw `text/plain` body, and a Routine delivers
`{"input": "…"}` JSON, both landing on the same `/invocations` endpoint where our AG-UI server
expects its own request shape. The result was a 415 from the Playground and Routines that ran the
agent with no prompt at all. Same mismatch, one layer deeper: the platform doesn't just assume an
agent *is* its chat protocol — it assumes the protocol is *its* protocol.

The resolution was an **adapter, not a fight**: the platform shapes are syntactically
self-identifying, so a small HTTP-layer middleware rewrites them into real chat turns before the
protocol handler binds — and routes every platform-originated run into a **throwaway context**, so
the one durable relationship can't be touched by a scheduled job or a portal test chat, by
construction. One more platform contract surfaced on the way: a Routine dispatch is a **30-second
acceptance handshake**, not a request that waits for the work — so the server acknowledges
immediately and runs the real task in the background. With that, the scheduled daily run finally
happened end-to-end, unattended: prompt delivered, sources scanned, digest emailed, checkpoints
updated, the durable context untouched.

And the open question above has tipped. The same forwarded route that carries the adapted platform
dialects can carry the three side operations too (header-dispatched before the chat handler) — and
the one gateway behavior that plan still needed proven, a non-protocol response body returning
through the live gateway, got proven in production as a side effect of the adapter work. The
companion comes off **without moving hosts and without a rewrite** — that removal is where the
series goes next.

## The quieter skirmishes (same shape, smaller)

- **Stale server-side conversation pointer** (`previous_response_not_found`) → a session-repair helper; later made moot by `store=false`.
- **TodoProvider and plan/execute mode** — harness defaults aimed at other product shapes (coding agents / planners) that derailed plain chat; both disabled.
- **Inspectable memory** — dropped the harness's per-session file-memory folder for a single user-editable `working/` store (one inspectable place, one toolset).
- **De-emphasizing "sessions"** — the session becomes an internal context-window detail, not a user-facing concept.

---

## An honest word on AG-UI

AG-UI was chosen to **learn it**, not because it's the obvious fit for a server-authoritative durable
assistant. Given that goal we are swimming upstream — and AG-UI is where the session-vs-relationship
mismatch is **most visible**, because it bakes *client-owns-history* into the client itself. That's
exactly why the approval path fought back: we inverted the model the client is built around.

A transport designed around *server-owned* sessions would fit this assistant more naturally. But
choosing the one that fights you is a legitimate way to learn where the edges are — and the friction
*is* the lesson: every fight above taught us something concrete about how MAF's pieces actually
compose under a relationship-shaped goal. None of it was wasted; it's the map you're reading now.

A later design review surfaced the cleanest possible exit, worth recording honestly: the AG-UI
"client" here is not a browser — it's a Blazor *Server* app, a trusted tier that could legitimately
own the durable state itself, with the agent stateless. That inversion would dissolve the AG-UI
fight (the client owning history is the protocol's native model) and the gateway fight (a stateless
agent is exactly what a protocol-only gateway wants) rather than winning them. We assessed it and
declined, for now: the durable context is more than a message list (it carries the compacted
history, encrypted reasoning items that preserve the model's reasoning continuity, and the pending
state that makes interactive approval resumable and server-verifiable), none of which rides the
protocol — so the inversion would re-break several hard-won behaviors and amount to re-platforming,
while cheaper moves capture the same prizes. The friction described above is therefore a *choice,
consciously renewed* — not a trap we failed to see.
