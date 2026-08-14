---
title: "Introduction"
description: What Undertow is, what it serves, and what it was ported from.
---

Undertow is a **Fluid Framework–compatible collaborative document service**
implemented in .NET. One process serves two wire protocols and a REST surface
from one port:

| Surface | Path | Clients |
|---|---|---|
| Socket.IO | `/socket.io/` | Official Fluid/Routerlicious drivers (Engine.IO v4 / Socket.IO v5) |
| Phoenix Channels V2 | `/socket/websocket` | `levee-driver` / `levee-client` |
| REST | various | Documents, deltas, token mint, git-like Historian storage |

It is the .NET reimplementation of **Floodgate**, the Gleam server it was
ported from, and is wire-compatible with the **Levee** Elixir server for the
Phoenix protocol. Wire compatibility is not a slogan here — it is the
governing constraint of the codebase, held in place by golden frame
transcripts captured from the reference server and by conformance suites that
run against a live Undertow process. See
[Conformance & fixtures](/manual/conformance/).

## What this manual covers

- [Operating procedure](/manual/operating-procedure/) — from clone to a
  verified running server.
- [Configuration](/manual/configuration/) — the full environment-variable
  surface.
- [Wire protocols](/manual/protocols/) — frame formats, topics, and observed
  wire semantics.
- [Conformance & fixtures](/manual/conformance/) — the recorded results and
  how to reproduce them.
- [Divergence notes](/manual/divergences/) — every deliberate departure from
  the reference servers.
- [Internal organization](/manual/architecture/) — the tiers and what lives
  in each.
- [Lineage](/manual/lineage/) — Floodgate, Levee, and this repository's
  extraction history.

## Distribution

Undertow ships as source. There is no package to install: clone
[the repository](https://github.com/tylerbutler/undertow) and build with the
.NET SDK, or let Docker Compose build the container for you.
