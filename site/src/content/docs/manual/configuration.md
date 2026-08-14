---
title: "Configuration"
description: The full UNDERTOW_* environment-variable surface, precedence rules, and defaults.
---

Configuration is read explicitly from environment variables at startup — not
from `IConfiguration` conventions. The authoritative source is
`src/Undertow.Server/UndertowConfig.cs`.

## Precedence

Per key: `UNDERTOW_*` → `FLOODGATE_*` → default.

The `FLOODGATE_*` fallback is transitional: it exists so one compose file can
drive either binary while the shadow differ compares them, and it will be
removed when the differ retires. Startup logs which spelling supplied each
value, so a stale `FLOODGATE_*` setting can never win silently.

One exception: **`PORT`** (the container/PaaS convention) wins over
`UNDERTOW_PORT` and `FLOODGATE_PORT`.

## Parameters

| Parameter | Default | Purpose |
|---|---|---|
| `UNDERTOW_JWT_SECRET` | — **required** | Tenant JWT signing secret. The server refuses to start without it. |
| `PORT` | `3000` | Listen port. Wins over `UNDERTOW_PORT`. |
| `UNDERTOW_BIND` | `localhost` | Bind address. |
| `UNDERTOW_TENANT_ID` | `fluid` | Tenant id used in topics and tokens. |
| `UNDERTOW_TOKEN_MINT_SECRET` | (unset) | Enables the token-mint REST endpoint when set. |
| `UNDERTOW_TOKEN_MINT_USER_ID` | `floodgate-token-mint` | User id in minted tokens. Wire-observable, so the default keeps the Floodgate spelling. |
| `UNDERTOW_TOKEN_MINT_USER_NAME` | `Floodgate Token Mint` | User name in minted tokens. Wire-observable; same reasoning. |
| `UNDERTOW_PUBLIC_URL` | `http://localhost:{port}` | Public URL advertised to clients. |
| `UNDERTOW_ALLOWED_ORIGINS` | (unset) | Origin allow-list for browser clients; `*` allows all. |
| `UNDERTOW_STORAGE_BACKEND` | `ets` | `ets` / `shelf` = SQLite (WAL); `memory` = in-process only. |
| `UNDERTOW_DATA_DIR` | `priv/undertow_data` | SQLite data directory. |
| `UNDERTOW_MAX_FRAME_BYTES` | `16777216` | Frame cap. Also advertised as `maxPayload` in the Engine.IO open and `maxMessageSize` in IConnected. |
| `UNDERTOW_MAX_CONNECTIONS_PER_IP` | `256` | Per-IP connection ceiling. `0` = unlimited. |
| `UNDERTOW_MAX_CONNECTIONS` | `4096` | Total connection ceiling. `0` = unlimited. |
| `UNDERTOW_MESSAGE_RATE` | `1000` | Messages/sec per connection. `0` = unlimited. |
| `UNDERTOW_MESSAGE_BURST` | `2000` | Message burst allowance. `0` = unlimited. |
| `UNDERTOW_JOIN_RATE` | `100` | Joins/sec. `0` = unlimited. |
| `UNDERTOW_JOIN_BURST` | `200` | Join burst allowance. `0` = unlimited. |
| `UNDERTOW_HEARTBEAT_INTERVAL_MS` | `30000` | Heartbeat cadence. |
| `UNDERTOW_HEARTBEAT_TIMEOUT_MS` | `60000` | Heartbeat deadline. |
| `UNDERTOW_DOC_IDLE_MS` | `300000` | Idle-document eviction window. |
| `UNDERTOW_COMPAT_RESTORE_MSN_FROM_SUMMARY` | `0` | Compat flag: set to `1` to restore msn from summary. |
| `UNDERTOW_OP_PRUNE_BELOW_SUMMARY` | `0` | Set to `1` to prune ops below the latest summary. |

Rate and connection limits treat `0` as "unlimited". Positive-integer
parameters fall back to their default when unset, unparseable, or
non-positive.
