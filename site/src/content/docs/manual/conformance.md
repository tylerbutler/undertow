---
title: "Conformance & fixtures"
description: The recorded conformance results, the gates that produce them, and the golden wire transcripts.
---

Wire compatibility is proven, not asserted. Two instruments hold Undertow to
the reference behavior:

1. **Golden wire fixtures** — raw frame transcripts captured from the Gleam
   Floodgate reference server by `tools/Undertow.WireDiff` (record mode),
   stored in `tests/fixtures/wire/`. These are the byte-level contract.
2. **Conformance suites** — Floodgate's Routerlicious and Phoenix/cross-mode
   suites, run against one live Undertow process.

## Recorded results

Recorded 2026-08-07 against Floodgate at `2687b5f`. Counts move with the
repository; the fixtures are the authority.

| Suite | Result | Remarks |
|---|---|---|
| Routerlicious conformance | 38 pass | 3 skipped, 1 todo — the reference suite's own baseline |
| Phoenix + cross-mode conformance | 7 pass | both protocols against one process |
| Drop-in parity vs. Levee suite | 53 / 54 | the one non-pass is an intentional 401 (ADR-009) |
| Readiness checks | 8 / 8 | |
| levee-example integration | 15 pass | levee-client against Undertow |
| Unit + integration tests | 187 pass | `dotnet test Undertow.slnx` |
| todo-list multi-user e2e | 9 / 9 | live browser flow through the real container loader |

## Running the gates

```sh
just test              # unit + integration tests
just test-dual-mode    # Floodgate's Routerlicious and Phoenix/cross-mode suites
```

`just test-dual-mode` builds and starts a local Undertow process, waits for
`/health`, then runs the conformance suites from a Floodgate checkout
(location configurable via `FLOODGATE_REPO`, default `../floodgate`).

## The fixture transcripts

Line prefixes: `>` sent to server · `<` received · `#` annotation ·
`>A` / `<B` / `<P` label the socket in multi-socket scenarios.

| File | Scenario |
|---|---|
| `rest-basics.txt` | `/health` (GET+HEAD), token mint, create document, session, deltas, missing auth |
| `socketio-write-connect-op.txt` | Engine.IO open, `40` connect, write-mode `connect_document` (IConnected), `submitOp` → sequenced op |
| `socketio-read-nack.txt` | read-mode connect, `submitOp` → 403 nack |
| `socketio-auth-failures.txt` | expired token, bad signature → `connect_document_error` |
| `socketio-unicode.txt` | non-ASCII + `<&>` in `user.name` — raw UTF-8 on the wire, no `\uXXXX` escapes |
| `signals-broadcast-targeted-leave.txt` | 2 Socket.IO + 1 Phoenix client; legacy broadcast signal; v2 targeted signal; disconnect → sequenced leave op |
| `phoenix-write-connect-op.txt` | two-phase join, `connect_document` push, `submitOp` → op push, heartbeat, `phx_leave`, `phx_close` |
| `phoenix-bad-vsn.txt` | `vsn=1.0.0` rejected before upgrade |

`SOURCE.txt` alongside the fixtures records the exact reference commit and
capture time. The fixtures were re-captured 2026-08-07 after the assert-0x4b2
fix landed in the Gleam reference (see
[Divergence notes](/manual/divergences/)).
