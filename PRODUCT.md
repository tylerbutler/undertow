# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Stack

Server (existing): .NET — F# pure protocol core (`Undertow.Protocol`), C# runtime/transports/storage/host (ASP.NET). SQLite (WAL) is the persistent storage default; chiseled Docker image for deployment.

Project site/docs (planned, not yet built): Astro/Starlight, deployed to Netlify. Confirmed by the user 2026-08-13.

## Users

- **Self-hosting developers**: want a lightweight, wire-compatible alternative to Routerlicious they can run themselves for Fluid Framework-based collaborative apps.
- **Protocol implementers**: study the Floodgate (Gleam) / Levee (Elixir) / Undertow (.NET) trio as cross-implementation references for the Fluid wire protocol, using the shared golden fixtures and conformance suites.

## Product Purpose

Undertow is a Fluid Framework-compatible collaborative document service: one process serves two wire protocols and a REST surface from one port —

- `/socket.io/` for official Fluid/Routerlicious drivers (Engine.IO v4 / Socket.IO v5)
- `/socket/websocket` for Phoenix Channels V2 (`levee-driver` / `levee-client`)
- REST for documents, deltas, token mint, and git-like Historian storage

It is the .NET reimplementation of Floodgate, wire-compatible with the Gleam Floodgate server it was ported from and with the Levee Elixir server for the Phoenix protocol. Success means byte-level wire parity: dual-mode conformance suites green, drop-in parity against the Levee suite, and live browser e2e flows (e.g. the todo-list e2e) passing identically to the reference servers.

## Positioning

- **.NET ecosystem fit**: the Fluid-compatible server for teams already on .NET — F# pure core, ASP.NET host, SQLite default, single binary, one port.
- **Learning/exploration**: the project is also a personal engineering exploration; positioning claims should stay modest and evidence-backed, not marketed beyond what the conformance record supports.

## Operating Context

- Developed and validated against a shared conformance apparatus: golden wire fixtures captured from the Gleam reference live in `tests/fixtures/wire/` (with recorded baseline pass counts and documented deliberate divergences).
- `just` drives the workflow: `just setup/build/test/run`, `just test-dual-mode` (Routerlicious + Phoenix/cross-mode suites against one Undertow process), `just test-levee-suite-vs-undertow` (drop-in parity).
- Configuration mirrors Floodgate's env vars one for one (`UNDERTOW_*`, transitional `FLOODGATE_*` fallback) so one compose file can drive either binary. `UNDERTOW_JWT_SECRET` is required.
- Runs as a chiseled (shell-less) container; healthcheck re-runs the binary in `--healthcheck` argv mode.
- Plan and design record: `docs/plans/2026-08-06-undertow-plan.md`. Extracted from `tylerbutler/levee` at `ec92d7d` with filtered history preserved.

## Capabilities and Constraints

- Sequencing/validation/nacks/signals ("spillway"), JWT minting ("signet"), git objects + Historian shapes ("silt"), initial-summary planning, document-channel decisions, origin policy — all in the pure F# tier with zero project references.
- Per-document sessions, registries, channel dispatch, broadcaster, sweepers, connection/rate limits in the runtime tier; two wire transports plus a fragmentation reader.
- Storage backends: memory and SQLite (`shelf`/`ets` map to SQLite).
- Wire compatibility is the governing constraint: divergences from the reference servers are deliberate, individually documented (e.g. ADR-009 401; Phoenix bad-vsn 403), and recorded in `tests/fixtures/wire/README.md`.
- Terminology: the water-infrastructure naming family (Floodgate, Levee, Undertow, spillway, silt, signet) is established project vocabulary.
- **No UI exists today.** The only anticipated user-facing surface is the project site/docs (see Stack). Ops dashboards and diagnostic-tool UIs were explicitly not selected as anticipated surfaces (2026-08-13).

## Evidence on Hand

- Conformance record (as of 2026-08-07): dual-mode 38 + 7 green; drop-in parity 53/54 with one intentional ADR-009 401; readiness 8/8; levee-example 15 green; 187 unit/integration tests; todo-list e2e 9/9.
- Golden wire fixtures and divergence log: `tests/fixtures/wire/` and `tests/fixtures/wire/README.md`.
- Detailed engineering narrative (port history, reference re-pinning, live bug findings): `docs/plans/2026-08-06-undertow-plan.md` — real material a docs site can draw on.
- No testimonials, adoption numbers, or benchmarks exist; future work must not fabricate any.

## Product Principles

1. **Parity is the product.** Wire compatibility with the reference servers, proven by shared fixtures and conformance gates, outranks any feature or presentation ambition.
2. **Claims stay evidence-backed.** Every compatibility or quality statement must trace to the conformance record or fixtures; modest, verifiable language over marketing.
3. **One process, one port, low ceremony.** Deployment simplicity (single binary, SQLite default, env-var config, chiseled container) is a durable commitment.
4. **Divergence is documented, never silent.** Any departure from reference behavior is deliberate and written down where implementers will find it.
5. **Serve implementers, not just operators.** The cross-implementation audience means internals, naming, and protocol decisions are part of the public story, not hidden plumbing.
