# MyAssistant — a Muse-style personal assistant on MAF's Agent Harness

A personal digital assistant built on **Microsoft Agent Framework (MAF)** and its **Agent
Harness**, inspired by consumer personal-agents like Meta's Muse and OpenAI's Dots (if far less
robust). It's a learning project, built in public alongside a LinkedIn series.

The north star: **one durable context, standing (durable) permissions, a hard human-only floor on
dangerous actions, and inspectable memory you can edit** — rather than a chatbot that forgets you
when the conversation ends.

## The series

Each post builds on the last; the code here is the companion.

- **Based on** — Roughly follows the series from [this MS Blog Post](https://devblogs.microsoft.com/agent-framework/build-your-own-claw-and-agent-harness-with-microsoft-agent-framework/)
- **My LinkedIn Post #1** — Setup, and making memory & permissions survive across sessions. _([Click here](https://www.linkedin.com/feed/update/urn:li:activity:7515815982616068096/))_
- Post #2 — _TBD (coming soon?)._


## Status

Early work in progress — the code reflects that.

- ✅ Durable, cross-session tool approvals ("standing permissions"), with an audit log and a
  non-overridable human-only tier.
- ✅ Durable, folder-pinned file memory (survives sessions instead of per-session folders).
- ⏳ End-goal: replace the console with a **Blazor + AG-UI** client talking to a separately-hosted
  agent. Not started.

## Running it

Requires the **.NET 10 SDK**.

1. Provide configuration via `appsettings.json` + `appsettings.Development.json` (not included).
   See `MyAssistant/Program.cs` for the exact keys it reads.
2. From the `MyAssistant` project folder: `dotnet run`.
3. Ask about a stock, or try: _"Review my watchlist and recommend some stocks to add."_ A sensitive
   tool call will prompt for approval — choose "always approve" to create a standing grant.

> Note: the included `StockTools.GetStockPrice()` was changed to require approval to allow easy testing of standing permissions.

> Note: `Microsoft.Agents.AI.*` are **preview** packages; member names can drift between versions.

## Project layout

| Path | What it is |
|---|---|
| `MyAssistant/` | The console app: agent wiring (`Program.cs`), the custom `get_stock_price` tool. |
| `MyAssistant.ApprovalPolicy/` | **Standalone, zero-dependency** library for the durable approval policy (rule store, canonicalizer, human-only tier, audit log). Framework-agnostic on purpose, so it ports to the AG-UI server later. |
| `Harness_Shared_Console*`, `ConsoleReactive*` | The MAF sample's console UI, **copied in** (it isn't published to NuGet) and lightly modified. See the detail doc. |

## How this relates to MAF

Two things are both called "harness" — keep them straight:

- **MAF harness** = the `Microsoft.Agents.AI.Harness` NuGet package / `AsHarnessAgent` — the
  "brain" (tool-calling, modes, memory, approval engine, session serialization). Referenced as
  packages; I only configure it.
- **The console projects** = the copied terminal UI shell — the only part I actually modify.

## Deeper detail

- **[`what-i-changed-in-the-sample.md`](./what-i-changed-in-the-harness.md)** — exactly what I
  changed in Microsoft's sample code and why: the durable approval design, the three data layers
  (file access vs. file memory vs. Foundry), how "durable"/"standing" differ between Microsoft's
  framing and this series, and a comparison to Muse.

## Caveats

- Preview packages (API drift), a work in progress, and a few "session"-era leftovers remain (e.g.
  the `/session-export` / `/session-import` commands) even though the session concept is being
  retired in future versions of this project.
- Not affiliated with Microsoft, Meta, or OpenAI. Muse/Dots references are based on public
  information.

  
---

## Copyright and License

### Code

Copyright (©) Jzuras

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

## Trademarks

All trademarks are the property of their respective owners.
Any trademarks used in this project are used in a purely descriptive manner and to state compatibility.
