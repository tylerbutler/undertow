# Golden wire fixtures

Raw frame transcripts captured from the **Gleam Floodgate** reference server by
`tools/Undertow.WireDiff` (`record` mode). These are the byte-level contract the
.NET Undertow implementation is held to. See `SOURCE.txt` for the exact source
commit and capture time. Captured from floodgate at Levee commit `2687b5f`
(> `22cf469`, so the second gap-closure landing — Engine.IO ping timeout,
op-history cap, idle eviction, `42["close"]`, signal targeting, per-document
sequencing — is baked in).

Transcript line prefixes: `>` sent to server, `<` received, `#` annotation.
Multi-socket scenarios label the socket (`>A` / `<B` / `<P`).

Re-captured 2026-08-07 after the 0x4b2 fix landed in Gleam floodgate:
`initialSignals` in IConnected is now always `[]` (matching levee), so the
fixtures no longer show the client's own presence-join signal there.

## Files

| File | Scenario |
|---|---|
| `rest-basics.txt` | `/health` (GET+HEAD), token-mint, create document, session, deltas, missing auth |
| `socketio-write-connect-op.txt` | Engine.IO open, `40` connect, write-mode `connect_document` (IConnected), `submitOp` → sequenced op |
| `socketio-read-nack.txt` | read-mode connect, `submitOp` → 403 nack |
| `socketio-auth-failures.txt` | expired token, bad signature → `connect_document_error` |
| `socketio-unicode.txt` | non-ASCII + `<&>` in `user.name` — raw UTF-8 on the wire, no `\uXXXX` escapes |
| `signals-broadcast-targeted-leave.txt` | 2 Socket.IO clients + 1 Phoenix client; legacy `{content}` broadcast signal (reaches **everyone incl. the sender**); v2 `contentBatches` signal targeting one client (reaches only that client, content keys re-sorted); disconnect → sequenced leave op |
| `phoenix-write-connect-op.txt` | two-phase join (`phx_reply` ok/empty), `connect_document` push, `submitOp` → op push, heartbeat on topic `phoenix`, `phx_leave`, `phx_close` |
| `phoenix-bad-vsn.txt` | `vsn=1.0.0` rejected before upgrade |

## Baseline conformance counts (Gleam Floodgate at `2687b5f`)

Recorded 2026-08-07 via `just test-floodgate-dual-mode`:

- Routerlicious suite: **38 passed**, 3 skipped, 1 todo
- Phoenix + cross-mode suite: **7 passed**

These are the counts Undertow must reproduce (Phases 5 and 7).

## Notable captured semantics

- `maxPayload` in the Engine.IO open, `maxMessageSize` in IConnected, and
  `serviceConfiguration.maxMessageSize` are all 16777216 — the single
  `FLOODGATE_MAX_FRAME_BYTES` value.
- The Fluid clientId **is** the Engine.IO `sid` (and the beryl socket id).
- IConnected claims omit `jti` and the user's `name` (signet's decoder keeps
  name in user properties, and the claims encoder emits only `id`).
- Join/leave ops have `clientId: null`, csn/rsn `-1`, `data` as a JSON *string*.
- Timestamps are `now_seconds() * 1000` — always ≡ 0 (mod 1000).
- v2 signal content objects come back key-sorted (`normalize_client_json`);
  legacy string signal content is passed through verbatim.
- A legacy broadcast signal is delivered to the sender too (plain `broadcast`,
  not `broadcast_from`).

## Deliberate divergence from these fixtures

- **Supplied IClient echo is verbatim, not key-sorted.** The Gleam fixtures
  show the client's IClient re-serialized with term-sorted keys (an Erlang
  map cannot preserve order); Undertow preserves the supplied key order.
  This mattered because the container-loader seeds its own audience entry
  with the object it *sent* (original key order) and assert 0x4b2 requires
  byte-identity with any later add for the same client id. The bug this
  caused — the loader receiving its own join back as an `initialSignals`
  presence signal in a different key order and closing the container — was
  fixed in *both* servers by making `initialSignals` always `[]`, matching
  levee (levee-todo-list multi-user e2e: 6/9 before, 9/9 after on both).
  Undertow keeps the verbatim echo anyway: every remaining echo path (join
  op `data`, `initialClients`, peer presence signals) then matches the
  client's own bytes too, which the sort can never do. The differ flags the
  key order inside those payloads as the one expected difference.
