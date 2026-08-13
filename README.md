# Undertow

Undertow is the **.NET reimplementation of Floodgate** — a Fluid
Framework-compatible collaborative document service. One process serves two
wire protocols and a REST surface from one port, wire-compatible with the
Gleam Floodgate server it was ported from (and with the Levee Elixir server
for the Phoenix protocol):

- `/socket.io/` — official Fluid/Routerlicious drivers (Engine.IO v4 / Socket.IO v5)
- `/socket/websocket` — Phoenix Channels V2 (`levee-driver` / `levee-client`)
- REST — documents, deltas, token mint, and git-like Historian storage

Plan and design record: `docs/plans/2026-08-06-undertow-plan.md`. The golden
wire fixtures captured from the Gleam reference (with the recorded baseline
pass counts and one documented deliberate divergence) live in
`tests/fixtures/wire/`.

## Layout

| Project | Language | Contents |
|---|---|---|
| `src/Undertow.Protocol` | F# | The pure tier: ordered JSON AST, spillway (sequencing/validation/nacks/signals), signet (JWT), silt (git objects + Historian shapes), initial-summary planning, document-channel decisions, origin policy. Zero project references. |
| `src/Undertow.Abstractions` | C# | `IDocumentStore` / `IGitObjectStore`, the etag'd `CommitSequencedAsync` seam |
| `src/Undertow.Runtime` | C# | Document sessions (per-document `SemaphoreSlim`), registries, channel dispatch, broadcaster, sweepers, limits |
| `src/Undertow.Transports` | C# | The two wire transports + fragmentation reader |
| `src/Undertow.Storage.Memory` / `.Sqlite` | C# | Storage backends (SQLite WAL is the persistent default) |
| `src/Undertow.Server` | C# | ASP.NET host: env config, RestLess middleware, auth, REST router |
| `tools/Undertow.WireDiff` | C# | Wire recorder + shadow differ against transcript directories |

## Build / test / run

```bash
dotnet build Undertow.slnx
dotnet test Undertow.slnx
UNDERTOW_JWT_SECRET=dev-tenant-secret-key dotnet run --project src/Undertow.Server
```

Or from the repo root: `just build-undertow`, `just test-undertow`,
`just undertow-server`, and the conformance gate `just test-undertow-dual-mode`
(38 Routerlicious + 7 Phoenix/cross-mode tests against one process).

## Configuration

Environment variables mirror Floodgate's one for one, spelled `UNDERTOW_*`;
each key transitionally falls back to its `FLOODGATE_*` spelling (so one
compose file can drive either binary), with the source of every value logged
at startup. `UNDERTOW_JWT_SECRET` is required. `PORT` wins over
`UNDERTOW_PORT`. See `src/Undertow.Server/UndertowConfig.cs` for the full
surface: frame cap, connection/rate ceilings, heartbeat cadence, idle-document
window, storage backend (`shelf`/`ets` = SQLite, `memory`), origin allow-list.

## Container

```bash
docker compose up -d --wait   # builds undertow:local, ReadyToRun, chiseled runtime
```

The chiseled image has no shell; the container healthcheck re-runs the binary
in `--healthcheck` argv mode.
