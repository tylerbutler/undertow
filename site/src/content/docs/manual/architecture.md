---
title: "Internal organization"
description: The pure F# protocol tier, the C# runtime, transports, storage backends, and the ASP.NET host.
---

The solution separates a pure, dependency-free protocol core from the
runtime, transports, and host that carry it.

| Project | Language | Contents |
|---|---|---|
| `src/Undertow.Protocol` | F# | The pure tier: ordered JSON AST, **spillway** (sequencing, validation, nacks, signals), **signet** (JWT), **silt** (git objects + Historian shapes), initial-summary planning, document-channel decisions, origin policy. Zero project references. |
| `src/Undertow.Abstractions` | C# | `IDocumentStore` / `IGitObjectStore`, the etag'd `CommitSequencedAsync` seam |
| `src/Undertow.Runtime` | C# | Document sessions (per-document `SemaphoreSlim`), registries, channel dispatch, broadcaster, sweepers, limits |
| `src/Undertow.Transports` | C# | The two wire transports plus the fragmentation reader |
| `src/Undertow.Storage.Memory` / `.Sqlite` | C# | Storage backends — SQLite (WAL) is the persistent default |
| `src/Undertow.Server` | C# | ASP.NET host: env config, RestLess middleware, auth, REST router |
| `tools/Undertow.WireDiff` | C# | Wire recorder + shadow differ against transcript directories |

## Naming

The water-infrastructure names are load-bearing vocabulary shared with the
Floodgate/Levee family:

- **spillway** — sequencing and validation: what flows through, in what
  order, and what gets nacked.
- **signet** — JWT encoding and decoding.
- **silt** — git objects and Historian storage shapes: what settles and is
  kept.

## Concurrency shape

Sequencing is per-document: each document session owns its ordering (a
per-document `SemaphoreSlim` in the runtime tier). This mirrors the
reference's one-actor-per-document design — the same shape, in .NET terms.

## Storage

`ets` and `shelf` map to the SQLite backend (WAL mode); `memory` keeps
everything in-process. Op history is capped and idle documents are evicted on
a configurable window (`UNDERTOW_DOC_IDLE_MS`), matching the reference's
behavior.
