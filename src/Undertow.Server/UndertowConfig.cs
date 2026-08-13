namespace Undertow.Server;

/// <summary>
/// Configuration read explicitly from environment variables at startup — not
/// IConfiguration conventions. Precedence per key: UNDERTOW_* → FLOODGATE_* →
/// default. The FLOODGATE_* fallback exists only so the Phase-0 shadow differ
/// can drive both binaries from one compose file; delete it when the differ
/// retires. Startup logs which spelling supplied each value.
/// </summary>
public sealed record UndertowConfig(
    int Port,
    string Bind,
    string Tenant,
    string JwtSecret,
    string? TokenMintSecret,
    string TokenMintUserId,
    string TokenMintUserName,
    string PublicUrl,
    string AllowedOrigins,
    string StorageBackend,
    string DataDir,
    long MaxFrameBytes,
    int MaxConnectionsPerIp,
    int MaxConnections,
    int MessageRate,
    int MessageBurst,
    int JoinRate,
    int JoinBurst,
    int HeartbeatIntervalMs,
    int HeartbeatTimeoutMs,
    long DocIdleMs,
    bool CompatRestoreMsnFromSummary,
    bool OpPruneBelowSummary)
{
    public const long DefaultMaxFrameBytes = 16_777_216;

    public static UndertowConfig FromEnvironment(Func<string, string?> getenv, Action<string>? log = null)
    {
        var report = log ?? (_ => { });

        string Get(string key, string fallback)
        {
            // Precedence: UNDERTOW_* -> FLOODGATE_* -> default; log the source
            // so a stale FLOODGATE_* value can't silently win.
            var undertowKey = $"UNDERTOW_{key}";
            var floodgateKey = $"FLOODGATE_{key}";
            if (getenv(undertowKey) is { Length: > 0 } fromUndertow)
            {
                report($"config {key}: from {undertowKey}");
                return fromUndertow;
            }

            if (getenv(floodgateKey) is { Length: > 0 } fromFloodgate)
            {
                report($"config {key}: from {floodgateKey} (transitional fallback)");
                return fromFloodgate;
            }

            return fallback;
        }

        // Positive integer or default (unset, unparseable, non-positive).
        long Positive(string key, long fallback) =>
            long.TryParse(Get(key, ""), out var v) && v > 0 ? v : fallback;

        // Limits: 0 means "unlimited" and must be preserved.
        int Limit(string key, int fallback) =>
            int.TryParse(Get(key, ""), out var v) && v >= 0 ? v : fallback;

        // PORT (the container/PaaS convention) wins over UNDERTOW_PORT/FLOODGATE_PORT.
        var port = int.TryParse(getenv("PORT"), out var p) ? p
            : int.TryParse(Get("PORT", ""), out var fp) ? fp
            : 3000;

        return new UndertowConfig(
            Port: port,
            Bind: Get("BIND", "localhost"),
            Tenant: Get("TENANT_ID", "fluid"),
            JwtSecret: Get("JWT_SECRET", ""),
            TokenMintSecret: Get("TOKEN_MINT_SECRET", "") is { Length: > 0 } mint ? mint : null,
            // These two default values are wire-observable (JWT claims and the
            // IConnected payload) — they stay "floodgate" regardless of the
            // project name. See the plan's auth section.
            TokenMintUserId: Get("TOKEN_MINT_USER_ID", "floodgate-token-mint"),
            TokenMintUserName: Get("TOKEN_MINT_USER_NAME", "Floodgate Token Mint"),
            PublicUrl: Get("PUBLIC_URL", $"http://localhost:{port}"),
            AllowedOrigins: Get("ALLOWED_ORIGINS", ""),
            StorageBackend: Get("STORAGE_BACKEND", "ets"),
            DataDir: Get("DATA_DIR", "priv/undertow_data"),
            MaxFrameBytes: Positive("MAX_FRAME_BYTES", DefaultMaxFrameBytes),
            MaxConnectionsPerIp: Limit("MAX_CONNECTIONS_PER_IP", 256),
            MaxConnections: Limit("MAX_CONNECTIONS", 4096),
            MessageRate: Limit("MESSAGE_RATE", 1000),
            MessageBurst: Limit("MESSAGE_BURST", 2000),
            JoinRate: Limit("JOIN_RATE", 100),
            JoinBurst: Limit("JOIN_BURST", 200),
            HeartbeatIntervalMs: (int)Positive("HEARTBEAT_INTERVAL_MS", 30_000),
            HeartbeatTimeoutMs: (int)Positive("HEARTBEAT_TIMEOUT_MS", 60_000),
            DocIdleMs: Positive("DOC_IDLE_MS", 300_000),
            CompatRestoreMsnFromSummary: Get("COMPAT_RESTORE_MSN_FROM_SUMMARY", "") == "1",
            OpPruneBelowSummary: Get("OP_PRUNE_BELOW_SUMMARY", "") == "1");
    }

    public static string Topic(string tenant, string doc) => $"document:{tenant}:{doc}";
}
