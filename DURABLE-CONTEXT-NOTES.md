# Durable context on the server — one continuous memory (and what it cost)

**Who should read this:** anyone building a *personal* agent on the Microsoft Agent Framework (MAF)
harness + AG-UI who wants the assistant to have **one continuous memory that survives restarts**, rather
than a pile of disconnected chat sessions. This is the companion to the LinkedIn build-series post on the
durable-context phase — the post stays concept-level; the specifics are here.

> Preview-software caveat: this is built on preview `Microsoft.Agents.AI.*` packages. Names and behavior
> drift between versions; treat the specifics as "true as of the versions in the repo," not as a stable
> API contract.

## Why one durable context (not per-session chats)

A chatbot can get away with per-session threads — open a new chat, start fresh. A *personal assistant*
can't. The whole value is that it remembers working with you: across restarts, across days, as one
continuous relationship. So the design goal is **one durable context**, owned by the server, that:

- survives the server restarting,
- is the same context regardless of which client (or client "thread") connects, and
- is **ours** — not something we rent from the model provider and lose when they expire it.

That last point turned out to matter more than expected (see "client-managed history" below).

## How it works

### One context via a custom session store

MAF lets you plug in an `AgentSessionStore`. Ours (`MyAgentSessionStore`) does one deliberate thing:
it **ignores the client's thread id and routes every request to a single fixed context**, which it
persists as a serialized session snapshot. There is intentionally no "new chat" by default — continuity
is the point. (A deliberate, memory-preserving "new context" reset is designed but deferred; see
*unfinished* below.)

### Crash-safe, swappable storage

The snapshot sits behind a tiny interface (`ISnapshotStore`) with one filesystem implementation today
and a blob-backed one intended for the cloud later. The filesystem version writes the snapshot with
**write-temp-then-atomic-rename** and keeps one prior generation, under a **single-writer guard**. Why
bother: MAF's store contract is a plain save/get of an opaque blob — there is **no** built-in
atomic-commit or compare-and-swap. For an assistant whose whole state is one file, a torn write between
"approval granted" and "result applied" would corrupt the one thing you can't lose. So crash-safety is
ours to engineer, not something inherited from the framework.

The snapshot lives in an app-relative `context/` folder (`session.json` + a `.prev` backup), kept
**separate from the user-editable `working/` store** — it's machine-owned, readable JSON you can inspect
but aren't meant to hand-edit.

### Client-managed history (`store=false`) — so the context is genuinely ours

The model provider's Responses API can store conversation history **on its servers** and hand you back a
pointer (`previous_response_id`). Convenient — but then your "durable context" is really leaning on the
*provider's* retention, and a provider-side expiry silently guts it. We instead run with
**`store=false` (client-managed history)**: the full transcript lives in **our** snapshot and is sent on
each turn. The context is then genuinely self-contained — it survives on *our* storage, independent of
provider retention — and a whole class of "the provider forgot my conversation" failures simply can't
occur. (Token cost is unchanged — the model bills the full history either way; only request bandwidth
grows, and prompt-prefix caching absorbs most of the cost.)

## The payoff: this closed the Part 2 security hole

Part 2 of the series ended on an honest asterisk: to make human-in-the-loop approval work across the
client/server split, I'd flipped a flag that **disabled a framework security protection**, and documented
that trade-off.

The durable store turned out to be the missing piece. Interactive approval is a network round-trip —
the agent pauses, the client answers in a *later* request — so the paused state must be persisted and
reloaded in between. With a **correct** durable store in place (atomic writes + single-writer), the
framework's approval binding survived that persist/reload after all, so I **re-enabled it and removed the
workaround** — regaining the server-authoritative guarantee. The asterisk from Part 2 is gone. Full
detail and the remaining multi-user caveats are in **`SERVER-SECURITY-NOTES.md`**.

## Keeping one long context bounded — compaction (and a trap)

One context that lives forever grows without bound, so it needs **context-window compaction**
(summarize/trim old turns before the window overflows). MAF can do this — but *how* you wire it matters,
and getting it wrong cost a full debugging cycle:

- **The trap:** setting the harness's `MaxContextWindowTokens` / `MaxOutputTokens` makes compaction run
  as a **separate, standalone pass** alongside the history store. In our setup that standalone pass
  **froze at the first turn** and fed the model a stale snapshot every turn — the assistant literally
  stopped seeing new messages.
- **The fix:** wire the *same* compaction strategy as the history provider's **reducer**, so there is
  exactly **one** mechanism operating on the live history. One mechanism, not two fighting over the same
  job.

**And this is the documented pattern — not a hack we stumbled into.** Wiring a reducer onto the history
provider is how Microsoft's own docs say to reduce in-memory history. The Learn *Storage* guide's
"Reducing in-memory history size" section
([learn.microsoft.com/…/agents/conversations/storage](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/storage))
shows exactly this shape — an `InMemoryChatHistoryProvider` built with a `ChatReducer`:

```csharp
ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
{
    ChatReducer = /* a reducer */
})
```

The doc's example uses a simple message-count reducer; we just plug in the **same**
`ContextWindowCompactionStrategy` the harness would otherwise auto-build (via `.AsChatReducer()`), so we
get real token-window compaction wired the documented way.

The subtle part — and the actual lesson — is that the framework gives you **two doors to the same room.**
Setting `MaxContextWindowTokens` / `MaxOutputTokens` is the convenient, automatic door; it spins up
compaction as a **separate, standalone** pass. Supplying the reducer on the history provider is the
explicit, documented door; compaction runs **inside** the one history mechanism. Same intended outcome —
but **only the automatic door walks into the bug.** Nothing in the docs warns that the two entry points
differ in this way, which is exactly why the bug is easy to hit and the fix easy to miss. So the
practical upshot for anyone hitting it: you don't have to turn compaction *off* — switch from the `Max*`
knob to the documented reducer wiring and you keep compaction while stepping off the buggy standalone
path.

This is a genuine framework defect, already tracked upstream as
[microsoft/agent-framework #7395](https://github.com/microsoft/agent-framework/issues/7395) (open) with a
fix in [PR #8838](https://github.com/microsoft/agent-framework/pull/8838) (open, unmerged): the standalone
compaction index early-returns when a provider re-injects an identical trailing message, so the new turn
is never indexed. Our minimal repro + full diagnosis are in **`DP7-UPSTREAM-ISSUE.md`** and
**`MyAssistant-DP7-RESOLUTION.html`**.

**Why we keep the reducer wiring even once #8838 ships:** that PR makes the *standalone* index correct;
the reducer approach removes that separate index as the prompt source altogether. With the reducer there
is **one** mechanism operating on the single live history — no second, parallel "compacted view" that can
drift from or shadow the real messages, so the whole class of index bugs (including this one) can't
manifest in the first place. And because the live messages (not a compacted index) are what the model
actually receives, compaction can only affect *summarization quality near the window limit* — it can never
silently freeze an ongoing conversation. It is also the framework's own documented default wiring (setting
`Max*` is precisely what spawns the standalone path), and it works today independent of a preview PR
landing, leaning far less on intricate index-fingerprinting machinery. The simpler `Max*` form stays
available if we ever want it — a preference, not a lock-in.

Lesson worth repeating: I'd "verified" the feature looked safe by *inspection* and shipped it; it only
broke when **exercised live, across multiple turns**. A static check is not a verified behavior.

## What's inspectable

The durable context is a single readable JSON file (`context/session.json`). If you ever need to debug
session state, **`MyAssistant-SESSION-SNAPSHOT-ANATOMY.html`** maps its structure and the diagnostic
red flags (e.g. a standalone-compaction block reappearing is the regression signature above). Note the
snapshots are **provider-specific** — clear `context/*.json` when switching model providers.

## What's deliberately unfinished (the honest part)

- **The client is now genuinely server-authoritative** (done since the first draft of this note). It sends
  only the new user turn — with a stable thread id so the server can correlate it to the one context — and
  lets the server own history; the earlier stop-gap that merely kept the client from re-sending its own copy
  each turn is gone. *A non-obvious cost surfaced here:* sending **only** the new turn starved the
  framework's **client-side** approval pairing (which reconstructs an approval from the request and response
  sitting together in the thread), so interactive approve/deny **broke again** until the client was changed
  to build the AG-UI *resume* itself and send it directly. Every simplification has a bill — full detail in
  **`MyAssistant-AGUI-APPROVAL-BLOCKER.html`**.
- **The client still doesn't *show* the stored conversation.** A freshly-opened web client starts blank —
  the server holds the full context but doesn't yet replay it to a connecting client for display. Making a
  new client paint the whole conversation on connect is the next chapter (and the deferred "new context"
  reset below is coupled to it).
- **A memory-preserving "new context" reset** is designed (archive the current snapshot, keep the
  `working/` memory + standing permissions) but deferred until that client work lands — its payoff is
  coupled to it.
- **User uploads / generated-file outputs** ride the same workspace storage when built; not built yet.

## Multi-user / exposed deployments

Everything here targets a **single, trusted user on a non-exposed server**. Several protections are
deliberately skipped that become hard requirements for multi-user or internet-exposed deployments
(per-caller session isolation, concurrency control on the store, memory-as-untrusted-input at scale).
Those are enumerated in **`SERVER-SECURITY-NOTES.md`** ("additional requirements for multi-user / exposed
deployments").
