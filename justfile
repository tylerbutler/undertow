# Undertow - standalone Fluid Framework-compatible .NET server

default:
    @just --list

setup:
    dotnet tool restore
    dotnet restore Undertow.slnx

build:
    dotnet build Undertow.slnx

test:
    dotnet test Undertow.slnx

format:
    dotnet format Undertow.slnx
    dotnet fantomas src tests

lint:
    dotnet format Undertow.slnx --verify-no-changes
    dotnet fantomas --check src tests

precommit:
    dotnet tool restore
    dotnet format Undertow.slnx --verify-no-changes
    dotnet fantomas --check src tests
    dotnet build Undertow.slnx -warnaserror
    dotnet test Undertow.slnx --no-build

run port="3000":
    #!/usr/bin/env bash
    set -euo pipefail
    export UNDERTOW_JWT_SECRET="${UNDERTOW_JWT_SECRET:-dev-tenant-secret-key}"
    export UNDERTOW_TOKEN_MINT_SECRET="${UNDERTOW_TOKEN_MINT_SECRET:-dev-token-mint-secret}"
    export PORT={{port}}
    dotnet run --project src/Undertow.Server

docker-build:
    docker build -t undertow:local .

up:
    docker compose up -d --wait --build

down:
    docker compose down -v

logs:
    docker compose logs -f undertow

# Run Floodgate's Routerlicious and Phoenix/cross-mode conformance suites
# against one Undertow process.
test-dual-mode:
    #!/usr/bin/env bash
    set -euo pipefail

    export FLOODGATE_JWT_SECRET=floodgate-routerlicious-compat-secret
    export FLOODGATE_TOKEN_MINT_SECRET=floodgate-routerlicious-mint-secret
    export UNDERTOW_JWT_SECRET=$FLOODGATE_JWT_SECRET
    export UNDERTOW_TOKEN_MINT_SECRET=$FLOODGATE_TOKEN_MINT_SECRET
    export UNDERTOW_STORAGE_BACKEND=memory
    export PORT=3000

    server_pid=""
    cleanup() {
        [ -n "$server_pid" ] && kill -- "-$server_pid" 2>/dev/null || true
    }
    trap cleanup EXIT INT TERM

    dotnet build src/Undertow.Server >/dev/null
    scripts/setsid-portable bash -c \
        'exec dotnet src/Undertow.Server/bin/Debug/net10.0/Undertow.Server.dll' &
    server_pid=$!

    for i in $(seq 1 30); do
        if curl --max-time 1 -sf http://localhost:3000/health >/dev/null; then
            break
        fi
        if [ "$i" = 30 ]; then
            echo "ERROR: Undertow server not ready after 30s." >&2
            exit 1
        fi
        sleep 1
    done

    cd "${FLOODGATE_REPO:-../floodgate}/client"
    pnpm test:routerlicious
    pnpm test:phoenix
