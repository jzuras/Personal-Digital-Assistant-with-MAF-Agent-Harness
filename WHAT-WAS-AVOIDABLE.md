# What was avoidable, and what wasn't (a kludge-attribution map)

**Companion to [`WHY-THIS-SHAPE.md`](WHY-THIS-SHAPE.md).** That note tells you *why* the assistant
is shaped the way it is — the goal, the pillars, and the fights each pillar picked with the
framework. This one asks the colder, retrospective question: **for each awkward part of the build,
which forcing function actually caused it — and would a different transport or host have avoided
it?** Read SHAPE first for the *why*; come here to find out how much of the awkwardness was a
*choice*, how much was a *bug*, and how much is simply *the point of the app*.

> Preview-software caveat: built on `AGUI.*` and preview `Microsoft.Agents.AI.*` packages. Specifics
> drift between versions. And the two counterfactual columns below ("would Responses have avoided
> it?", "would Container Apps have avoided it?") are **reasoned, not benchmarked** — treat them as
> honest architectural judgement, not measured results.

---

## The thesis (the walk-back)

I once called the companion app — a separate process holding three custom operations the chat
gateway wouldn't carry — "duct tape and rubber bands," and I half-believed it was evidence that the
whole design was wrong: that durable state shouldn't be ours to own, that the storage model needed
inverting. It wasn't. **It was the symptom of exactly one host choice** — Foundry's protocol-only
gateway refusing custom routes — and it comes off *without* a rewrite, by serving those routes on
the one route the gateway does forward (see SHAPE's Fight 4, "round two").

So I'd mislabeled the symptom. The thing I thought needed re-architecting — who owns durable state —
was fine. What produced the mess was a handful of causes I'd lumped together: a genuine framework
bug, my own deliberate one-durable-context requirement (which I stand by), the AG-UI transport, and
the Foundry Hosted-Agent runtime. Only a couple were avoidable, each at a different cost. This note
separates them.

---

## The forcing functions

Every awkward part traces to one or more of these. Five real causes plus one *derived* one.

| Cause | What it is | Fair to own? |
|---|---|---|
| **ONE-CONTEXT** | The deliberate requirement that the assistant is one durable, continuing relationship that is *ours* — not a provider-held thread, not per-session chats. | **Yes — it's the entire point.** Costs that trace here are features, not debt. |
| **THE BUG** | A real framework defect: standalone context-window compaction froze at turn 1 when a message-injecting provider was active (upstream microsoft/agent-framework #7395, fix in PR #8838). | Not mine — but I hit it *because of* one-context (I needed compaction) plus the injecting provider on my path. |
| **AG-UI** | The UI↔agent transport. Its defining trait: the client SDK bakes in *client-owns-history*. | A choice — picked to learn it, openly not the obvious fit for a server-owned durable assistant. |
| **HOSTED AGENT** | Foundry Agent Service as the runtime: managed infra, scale-to-zero, a *protocol-only gateway* that forwards only declared routes. | A choice — and one Microsoft is actively pushing, with real bundled benefits (see the counterweight below). |
| **SPLIT** *(derived)* | A web UI talking to a separately-hosted agent. **Not an independent knob** — `split ⟺ (AG-UI OR Hosted)`. Either choice entails it; the only way out is an in-process agent (what the console does). | Entailed, not separately selected. Named only so costs inherent to "being networked at all" get attributed honestly. |
| **MAF HARNESS** | The Agent Framework harness's defaults, aimed at other product shapes (coding agents, planners). | Framework shape — neither my requirement nor the transport/host. |

*On the split, to pre-empt a fair objection:* the approval persist/reload workaround is
*split-generic* — true of any networked split, including a bespoke or Responses-based one — so it
would **not** get cheaper by dropping AG-UI, only by collapsing to in-process. Everywhere below,
read "split" as shorthand for "a cost of having chosen AG-UI or Hosted at all."

---

## The attribution matrix

Each awkward part, its owner(s), and the two counterfactuals that matter: would it survive swapping
**AG-UI → a server-session transport** (Responses or a bespoke HTTP/SSE API), and swapping
**Hosted Agent → Container Apps**?

| Kludge / awkward part | Owner | Survives Responses instead of AG-UI? | Survives Container Apps instead of Hosted? |
|---|---|---|---|
| **The companion app** (3 custom ops in a separate process) | HOSTED | Yes — unrelated to transport | **No — gone.** Custom routes co-locate on the agent |
| **Server-authoritative client inversion** (delta turns, null session, suppressing the client's own thread) | AG-UI × ONE-CONTEXT | **No (in this form)** — a server-history transport fits one-context without the fight | Yes — host-independent |
| **Caller-built `AGUIResume`** via `RawRepresentationFactory` (approval resume hand-wired) | AG-UI × ONE-CONTEXT | **No** — the sharpest "AG-UI tax"; the client-side pairing starves only because we inverted AG-UI's model | Yes — host-independent |
| **Warm-up probe + `/readiness` mapping** | HOSTED | Yes — unrelated to transport | **No** — a normal health endpoint works, but you lose the free scale-to-zero warm it doubles as |
| **Compaction wired as the history-provider reducer** (not the `Max*` knobs) | THE BUG + ONE-CONTEXT | Mostly yes — the *need* is one-context; the reducer wiring is the preferred path regardless | Yes — host-independent |
| **`store=false` downstream** (hosted-tool history stripping, encrypted-reasoning carry, provider-specific snapshot clearing) | ONE-CONTEXT | Yes — same Responses hop, same `store=false` | Yes |
| **Approval persist/reload across a split** (HITL decision must survive a round-trip) | SPLIT (amplified by Hosted) | Partly — inherent to any split; pairing details differ by transport | Partly — the split causes it; the host amplifies it |
| **All durable state externalized to Blob** (can't trust the sandbox FS) | HOSTED + ONE-CONTEXT | Yes | Mostly yes — a scale-to-zero ACA container is ephemeral too, so you externalize regardless |
| **Deploy complexity** (multi-identity RBAC, container-mode build, no-secrets-in-image) | HOSTED (partly general cloud) | Yes | Partly — ACA has its own, generally lighter, identity/registry story |
| **Foundry-invoker adapter + fire-and-accept dispatch** (Playground `text/plain` & Routine `{"input":…}` rewritten to the real request shape; 30s acceptance handshake → background run) | HOSTED × AG-UI | **Mostly no** — the dialect mismatch exists because *our* protocol on the forwarded route isn't the platform's native one | **No — but so are Playground and Routines.** The need vanishes *with the features* |
| **MAF defaults disabled** (TodoProvider, plan/execute mode) | MAF HARNESS | Yes | Yes |
| **Durable session store, atomic writes, single-writer guard** | ONE-CONTEXT | Yes | Yes |
| **Approval policy lib, human-only floor, arg canonicalization** | ONE-CONTEXT | Yes | Yes |

---

## What was actually avoidable

Only three items, and they split cleanly by owner:

- **The two AG-UI items** — the server-authoritative inversion and the hand-built `AGUIResume`.
  Both are downstream of a single root: AG-UI's .NET client is built around *client-owns-history*,
  and one-context inverts it. A transport designed around *server-owned* sessions dissolves both —
  the server owns history, the client is thin, neither the inversion nor the hand-built resume
  exists. **Avoidable, at the cost of not using AG-UI** (and not learning it, which was an explicit
  goal). The honest read isn't "AG-UI was a mistake" — it's that its central assumption is the
  opposite of this app's, so its costs landed exactly on the history/approval seam. Mechanics in
  SHAPE's Fight 3 and [`AGUI-APPROVAL-NOTES.md`](AGUI-APPROVAL-NOTES.md).

- **The companion app** — 100% a Hosted-Agent consequence, nothing to do with AG-UI, one-context,
  or the split. On Container Apps the three ops co-locate and the companion never exists. And it's
  removable *on Hosted too*, without a rewrite — by serving those ops on the one route the gateway
  forwards. That's the walk-back's center of gravity: the duct tape comes off with a dispatch
  middleware, not a re-architecture. Mechanics in SHAPE's Fight 4.

Everything else was either a bug (now fixed, on the preferred path) or not avoidable at all.

## What wasn't avoidable — and shouldn't be

These trace to one-context, and they'd be identical with **any** transport and **any** host. Owning
them is owning the app's purpose, not carrying debt:

- `store=false` and its whole downstream (hosted-tool stripping, encrypted-reasoning carry,
  provider-specific snapshots) — the cost of "the context is ours, not the provider's."
- The durable session store with atomic temp-then-rename writes and a single-writer guard.
- Context-window compaction — one long thread must be bounded.
- The entire durable-permissions stack — policy library, human-only floor, exact-args
  canonicalization for cross-session matching. "Always means forever" *requires* persistence.

None of this is duct tape; it's the load-bearing structure. The clean line: **the parts that might
look over-engineered are the parts that make it a durable assistant instead of a chatbot — and they
don't change no matter what I host it on or talk to it with.**

## The counterweight — what Hosted Agent bought

So this isn't a Hosted hit piece. The gateway tax paid for things a generic host would make you
build: **Routines** (a native cron scheduler — the daily-run trigger; on ACA you'd stand up Jobs or
a timer Function yourself), managed **scale-to-zero + identity + the runtime/gateway** bundled
rather than assembled, and the session sandbox lifecycle and protocol endpoints provided. (And likely
more stuff available in the Portal like Skills and Tools that I have yet to play around with, though
portal-vs-code leads to the "how would this work when testing locally?" question.) And the
one Hosted cost that actually hurt — the companion — is the one the multiplex retires, leaving the
bundled benefits with the bill mostly paid.

---

## The scorecard

| Cause | Avoidable? | At what cost | Net verdict |
|---|---|---|---|
| **ONE-CONTEXT** | No — it's the requirement | — | Keep. Its costs are the product. |
| **THE BUG** | Already handled | One debugging cycle; now on the preferred path | Resolved; upstream-tracked. |
| **AG-UI** | Yes — a server-session transport avoids the two inversion kludges | Lose a purpose-built agentic-UI transport + the learning goal | A real, localized tax — paid knowingly. |
| **HOSTED AGENT** | The companion is; the rest is platform baseline | Multiplex removes the companion for ~a middleware; or move to ACA and lose Routines | **The multiplex is the fix — no rewrite.** |
| **SPLIT** | Not independently — entailed by AG-UI/Hosted | Only all-in-one (agent in the UI process, i.e. the console) | A consequence, not a separate choice; its one live cost is resolved. |
| **MAF HARNESS** | Config, not rework | A few disable flags | Minor. |

**Bottom line.** I mislabeled the symptom. The companion looked like proof that storage ownership
was wrong and needed inverting; it was actually proof that *one route was blocked by one gateway*.
Everything I'd call solid — the durable context, the standing permissions, the inspectable memory —
was never the problem, and it's identical whether I use AG-UI or not, Foundry or not. The genuinely
awkward parts split cleanly: two are an AG-UI tax I paid on purpose to learn it, one was a framework
bug that's now fixed, and the one that stung — the companion — comes off with a dispatch middleware.
No rewrite. The duct tape was holding together a seam that didn't need re-cutting — just a smaller
piece of tape in the right place.
