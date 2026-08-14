---
title: "Wire protocols"
description: Socket.IO and Phoenix Channels V2 frame formats, document topics, and observed wire semantics.
---

One process serves both protocols and REST from one port.

## Socket.IO — `/socket.io/`

Engine.IO v4 / Socket.IO v5, as spoken by the official Fluid and
Routerlicious drivers. An event frame such as `42["op",…]` decomposes as:

| Field | Meaning |
|---|---|
| `4` | Engine.IO MESSAGE packet type |
| `2` | Socket.IO EVENT packet type |
| `["op", …payload]` | JSON array: event name, then arguments |

Observed semantics, pinned by the golden fixtures:

- The Fluid `clientId` **is** the Engine.IO `sid`.
- `maxPayload` in the Engine.IO open, `maxMessageSize` in IConnected, and
  `serviceConfiguration.maxMessageSize` all carry the single configured frame
  cap (default `16777216`).
- The Engine.IO ping enforces `pingInterval + pingTimeout` as a deadline; a
  missing pong disconnects.
- `42["close"]` is emitted on server-initiated close.
- IConnected claims omit `jti` and the user's `name` (the claims encoder
  emits only `id`).
- Join/leave ops have `clientId: null`, csn/rsn `-1`, and `data` as a JSON
  *string*.
- Timestamps are whole seconds times 1000 — always ≡ 0 (mod 1000).

## Phoenix Channels V2 — `/socket/websocket`

The protocol spoken by `levee-driver` and `levee-client`. Every frame is a
five-element JSON array:

```
[join_ref, ref, topic, event, payload]
```

- Document topics take the form `document:{tenant}:{documentId}`.
- Joining is two-phase: `phx_join` → `phx_reply` (ok/empty), then a
  `connect_document` push.
- Heartbeats run on the reserved topic `phoenix`.
- A join with `vsn=1.0.0` is rejected **403** before the WebSocket upgrade
  (see [Divergence notes](/manual/divergences/)).

## Signals

- A **legacy** `{content}` broadcast signal reaches everyone *including the
  sender* (plain broadcast, not broadcast-from).
- A **v2** `contentBatches` signal can target a single client and reaches
  only that client; v2 signal content objects come back key-sorted, while
  legacy string content passes through verbatim.

## REST

Served from the same port: document creation and sessions, delta (op)
retrieval, token minting (when `UNDERTOW_TOKEN_MINT_SECRET` is set), a
`/health` endpoint, and git-like Historian storage endpoints backed by the
configured storage backend.
