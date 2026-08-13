# Dockerfile for Undertow — the .NET reimplementation of Floodgate.
#
# Undertow is wire-compatible with Gleam Floodgate: one process serves the
# official Fluid/Routerlicious drivers over /socket.io/ and Phoenix Channels
# clients (levee-driver/levee-client) over /socket/websocket, plus the REST
# surface. See docs/plans/2026-08-06-undertow-plan.md.
#
# Build from the undertow directory:
#   docker build -t undertow:local server/undertow

# === Stage 1: Build ===
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS builder

WORKDIR /build

# Restore first so source edits don't invalidate the dependency layer.
COPY Undertow.slnx Directory.Build.props Directory.Packages.props ./
COPY src/Undertow.Protocol/Undertow.Protocol.fsproj src/Undertow.Protocol/
COPY src/Undertow.Abstractions/Undertow.Abstractions.csproj src/Undertow.Abstractions/
COPY src/Undertow.Runtime/Undertow.Runtime.csproj src/Undertow.Runtime/
COPY src/Undertow.Transports/Undertow.Transports.csproj src/Undertow.Transports/
COPY src/Undertow.Storage.Memory/Undertow.Storage.Memory.csproj src/Undertow.Storage.Memory/
COPY src/Undertow.Storage.Sqlite/Undertow.Storage.Sqlite.csproj src/Undertow.Storage.Sqlite/
COPY src/Undertow.Server/Undertow.Server.csproj src/Undertow.Server/
RUN dotnet restore src/Undertow.Server/Undertow.Server.csproj

COPY src src

# ReadyToRun, not NativeAOT — the F# tier (FSharp.Core reflection, printf)
# does not survive AOT.
RUN dotnet publish src/Undertow.Server/Undertow.Server.csproj \
    -c Release -o /app -p:PublishReadyToRun=true

# Empty, app-owned /data for the chiseled runtime stage (which has no shell).
RUN mkdir /data-init && chown 1654:1654 /data-init

# === Stage 2: Runtime ===
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime

WORKDIR /app
COPY --from=builder /app ./
COPY --from=builder --chown=1654:1654 /data-init /data

# Persistent SQLite storage. Declared as a volume so document history survives
# container replacement; set UNDERTOW_STORAGE_BACKEND=memory for ephemeral runs.
# The chiseled image has no shell, so /data is created in the builder stage and
# copied in owned by the app user (uid 1654 in the chiseled aspnet images).
ENV UNDERTOW_DATA_DIR=/data

# Undertow refuses to start without an explicit JWT secret, so there is no
# default here on purpose — supply UNDERTOW_JWT_SECRET at run time.
ENV PORT=3000
ENV UNDERTOW_BIND=0.0.0.0
ENV UNDERTOW_TENANT_ID=fluid
ENV UNDERTOW_STORAGE_BACKEND=shelf

EXPOSE 3000
VOLUME ["/data"]

# The chiseled image has no shell or wget, so the healthcheck re-runs this
# binary in --healthcheck argv mode (GET http://127.0.0.1:$PORT/health).
HEALTHCHECK --interval=5s --timeout=3s --start-period=15s --retries=5 \
    CMD ["/app/Undertow.Server", "--healthcheck"]

ENTRYPOINT ["/app/Undertow.Server"]
