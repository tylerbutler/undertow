---
title: "Operating procedure"
description: From clone to a verified running Undertow server, with Docker or the .NET SDK.
---

## 1 · Obtain the source

Undertow ships as source — there is no package to install.

```sh
git clone https://github.com/tylerbutler/undertow.git
cd undertow
```

## 2 · Start the server

### With Docker (recommended)

```sh
docker compose up -d --wait
```

This builds the `undertow:local` image from local source (ReadyToRun, on a
chiseled runtime image) and waits for the healthcheck. The compose file sets a
complete development environment: port `3000`, tenant `fluid`, the development
JWT secret, and SQLite storage on a named volume.

The chiseled image has no shell, so the container healthcheck re-runs the
binary itself in `--healthcheck` argv mode.

```sh
docker compose logs -f undertow   # follow logs
docker compose down -v            # stop and drop the storage volume
```

### With the .NET SDK

```sh
UNDERTOW_JWT_SECRET=dev-tenant-secret-key dotnet run --project src/Undertow.Server
```

`UNDERTOW_JWT_SECRET` is the one required setting; the server refuses to start
without it. `dev-tenant-secret-key` matches the development secret the Levee
integration suites use — set your own anywhere that is not a laptop.

With [just](https://github.com/casey/just) installed, `just run` exports the
development secrets and starts the server; `just setup`, `just build`, and
`just test` cover the rest of the workflow.

## 3 · Verify

```sh
curl http://localhost:3000/health
```

## 4 · Connect a client

Point either kind of client at `http://localhost:3000`:

- an **official Fluid/Routerlicious driver**, which connects via
  `/socket.io/`, or
- a **levee-client / levee-driver**, which connects via `/socket/websocket`
  using Phoenix Channels V2.

Both speak to the same process and the same documents. REST endpoints for
documents, deltas, token minting, and Historian storage are served from the
same port.
