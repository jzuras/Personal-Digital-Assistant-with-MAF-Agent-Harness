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

Two fights dominate — **durable context** and **durable permissions** — and this series' latest
battle is where the two **collide**, over AG-UI.

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
        │                                        ⚔ FIGHT 3 (this series' latest):
        │                                         interactive approval RESUME over AG-UI
        │                                         — the point where the two pillars collide
        │                                                           ▲
        └── pillar: durable PERMISSIONS ────────────────────────────┘
              • "always" must mean forever, across sessions
                 └─ ⚔ FIGHT 2: making standing permissions genuinely durable
              • and the grant/deny must round-trip to a UI over AG-UI

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
Muse-style *semantic scope-keys*, and the client-side "remember permanently" *write* path over AG-UI
(today grants are seeded in the file or made console-side).

→ Deep dive: **`what-i-changed-in-the-harness.md`**.

## Fight 3 — where they collide: interactive approval resume over AG-UI

This series' latest battle, and it belongs to **both** pillars at once. Durable context forced the
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
