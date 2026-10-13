# What I changed in the MAF Agent Harness sample (and why)

Companion notes for the LinkedIn series on building a Muse-style personal assistant on
Microsoft's Agent Framework (MAF) and its **Agent Harness**. The posts stay high-level; this
file is the detail.

Starting point: Microsoft's "Build your own claw and agent harness" samples, Part 2 —
["Working with your data, safely"](https://devblogs.microsoft.com/agent-framework/agent-harness-working-with-your-data-safely/).

---

## First, two things both called "harness"

They are different layers, and the word spans both:

- **`Microsoft.Agents.AI.Harness`** — the NuGet package. The "brain": `AsHarnessAgent(...)` plus
  batteries (tool-calling, plan/execute modes, to-do list, file memory, web search, approval
  gating, session serialization).
- **The copied console projects** (`Harness_Shared_Console*`, `ConsoleReactive*`) — just the
  terminal UI shell. It renders prompts and relays your answers back to the agent.

Microsoft's samples wire the console UI with **project references, not NuGet packages**, so to
build on them I copied those shared console projects into my solution. The console projects are
mine to modify; the NuGet "brain" I only configure.

---

## A terminology note: "durable" and "standing"

Microsoft and I use these words differently, and the difference is the whole point of Post #1.

| Term | Microsoft (Part 2) | This series |
|---|---|---|
| **"durable"** | Used for *memory* that persists on disk across runs. File memory's durability actually **depends on `/session-export` + `/session-import`** to re-link; Foundry memory is user-scoped. | Anything that survives **across conversations without session continuity** — standing, decoupled from any one chat. I apply it to **both** memory **and** permissions. |
| **"standing" (approvals)** | "standing approvals" — but these are **session-scoped**; they die with the session. | I made standing permissions **genuinely cross-session** — which is what Meta's Muse means by "standing permissions." |

So the harness ships approvals it *calls* "standing" that only last a session. Post #1 makes
them live up to the name.

---

## Change 1 — Durable standing permissions

**Problem:** the harness's "Always approve this tool" writes into the session's approval state,
so a granted permission dies when the session ends (it only survives via session export/import).

**What I built:** a standalone, dependency-free policy library plus one generic predicate.

- **One predicate, grants as data.** You don't turn each approval into code. A single
  `AutoApprovalRules` predicate consults a small JSON file (`working/approval-rules.json`) on
  every gated call — including a *new* session's first call. That file is where grants live.
- **"Always" means forever**, not "until this session ends." I'm de-emphasizing the session as a
  user-facing concept (see Change 3), so a lasting grant shouldn't be scoped to one.
- **Exact-argument only.** There is no blanket "approve this tool for any arguments." A grant is
  the tool name + canonicalized arguments. (Trade-off below.)
- **Permanence is a deliberate, separate step.** The quick path stays one-off; making a grant
  permanent is a second confirmation that also tells you exactly which file to edit to revoke it.
- **A non-overridable "human-only" floor.** Tools flagged human-only can never become a durable
  grant, no matter what you click. (Empty for now — no money/credential tool exists yet.)
- **Audit + revoke.** Every silent auto-approval is logged; you revoke a grant by deleting a line
  from the JSON file.
- **A safety cap.** `MaxAutoApprovalIterations` bounds how many auto-approvals can fire in a
  single run, so a standing grant can't quietly drive an unbounded loop of billable model calls.
  (I pinned it to the framework default explicitly, so a package update can't silently change it.)

**The honest trade-off vs. Muse.** Mine is *semantics-blind*. Muse can say "allow emails to this
one recipient, forever" because something classifies the action's meaningful scope. I have no such
taxonomy (or runtime classifier), so I match on exact arguments — strictly safer, but it re-asks
whenever anything changes. A deliberate first-attempt simplification, not the finish line.

**Why a standalone library.** The policy engine has no console/Blazor/UI dependencies, so it
ports unchanged when the UI is split off from the agent (see AG-UI below).

### The two console files I changed (kept backward-compatible)

- **`ToolApprovalObserver`** — gained a "durable" mode: a three-choice prompt (approve once /
  always / deny) and the second confirm step that writes the durable grant. When no durable store
  is supplied it behaves exactly as the original (session-scoped), so the sample's other steps are
  unaffected.
- **`HarnessConsoleOptions`** — an optional parameter to inject a pre-built approval observer,
  instead of always constructing the default one.

---

## Change 2 — Durable, inspectable memory (and the three data layers)

Microsoft frames **three** data mechanisms, and only two of them as "memory":

| Layer | Microsoft's framing | Who writes it | Default persistence | What I changed |
|---|---|---|---|---|
| **File access** (`file_access_*`) | "your data" — **not** memory | User-directed (agent acts on command) | Rooted at a store (I use `working/`) — already durable | Moved the **watchlist** here, out of file memory |
| **File memory** (`file_memory_*`) | Memory — coarse, explicit | **Agent** decides what to save | `agent-file-memory/<session-id>/` — session-scoped; needs export/import to survive | **Folder-pinned** → durable at `memory/durable/`, no session tricks |
| **Foundry memory** | Memory — fine, automatic | Automatic extraction | User-scoped, cross-session | Deferred (not used) |

**Moving the watchlist to file access.** The watchlist is a *document the user owns*, not a *fact
the agent learned*, so file access is its honest home. I did this by changing the instructions to
point at the `file_access` tools and stop calling it "memory." Note this crosses Microsoft's own
line — they treat file access as data, not memory — but from the assistant's point of view it's
durable knowledge either way.

**Making file memory durable.** File memory's session-scoped folder is the real durability pitfall
(its persistence leans on session export/import). Pointing a custom store at a fixed folder is
**not** enough — the provider still mints a per-session `{timestamp}_{guid}` subfolder under any
store root. The fix: disable the built-in provider (`DisableFileMemory`) and register my own
`FileMemoryProvider` whose `stateInitializer` **pins** `FileMemoryState.WorkingFolder` to a
constant. Now every session reads/writes the same `memory/durable/` folder.

**So do we have "two durable memories"?** In Microsoft's taxonomy, no — one durable *memory* (file
memory) plus a durable *document* store (file access). From the assistant's point of view, yes —
two durable stores with different roles: user-directed documents vs. agent-curated facts. That
split mirrors Muse, which manages both files/artifacts and learned facts. I'm keeping file memory
(pinned) for the agent-curated case, and watching whether it earns its place now that the watchlist
has moved out of it. Foundry memory stays deferred (it's automatic, opaque, and Azure-bound).

---

## Change 3 — De-emphasizing "sessions"

A Muse-style assistant is one continuous relationship, not a string of sessions. Permissions and
durable memory now attach to the assistant, not to a conversation. The session becomes an internal
context-window detail.

**Honest leftover:** the `/session-export` and `/session-import` commands still exist. They're
harmless and occasionally handy, so they stay for now even though "session" is no longer a concept
I want the user to think about. (MAF also has built-in context-window *compaction* via token-limit
options, which de-risks a single long-lived thread — a topic for later.)

---

## Change 4 — Policy Engine

- The policy engine is a separate project with **zero external package references** — plain C#
  over `System.Text.Json` — precisely so it survives the move to a hosted agent.

---

## Where this is heading: AG-UI

The console is not the end-state; it's just where it's fastest to get the behavior right. The real
target is a **UI-only client talking to a separately-hosted agent over AG-UI** (Microsoft's
agent-to-UI protocol). Everything above was built to survive that split: the policy engine is
framework-agnostic, and the memory/permission state lives in files the server can own. The open
question for that move — does a tool-approval request cross AG-UI to the client and back as a
human-in-the-loop round-trip? — is the thing I'll have to probe before the UI can render approvals.
