---
title: "Lineage"
description: Floodgate, Levee, and how this repository came to stand alone.
---

Undertow is the third implementation in a family that pins one wire protocol
across three runtimes:

| Server | Runtime | Relationship |
|---|---|---|
| **Levee** | Elixir / BEAM | Origin of the Phoenix-protocol surface; its integration suites are Undertow's drop-in parity bar |
| **Floodgate** | Gleam / BEAM | The reference Undertow was ported from; source of the golden wire fixtures |
| **Undertow** | .NET | This server — wire-compatible with both |

## The port

Undertow was ported from Floodgate at commit `22cf469`, with golden fixtures
captured at `2687b5f`. The plan and design record —
`docs/plans/2026-08-06-undertow-plan.md` in the repository — is unusually
candid engineering history: it was re-pinned **twice** because the reference
moved underneath it, and in four cases the advice inverted (ping timeouts,
`42["close"]`, op-history caps and idle eviction, and per-document
sequencing all became parity requirements after first being things to skip).

The port also surfaced a live bug in the reference: the Gleam server's
key-sorted IClient echo tripped the Fluid container loader's assert `0x4b2`
in real browser flows. The finding, and the fix that landed in both servers,
are recorded in [Divergence notes](/manual/divergences/).

## Extraction

This repository was extracted from
[`tylerbutler/levee`](https://github.com/tylerbutler/levee) at commit
`ec92d7d`; the filtered history preserves the original Undertow commits.

## Compatibility contract

Environment variables mirror Floodgate's one for one, spelled `UNDERTOW_*`
with a transitional `FLOODGATE_*` fallback, so one compose file can drive
either binary during shadow-diff runs. The `tools/Undertow.WireDiff` recorder
and differ exist precisely to keep that claim honest.
