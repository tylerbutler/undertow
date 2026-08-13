namespace Undertow.Runtime;

/// <summary>
/// The per-socket and per-peer guards both transports share, so the two
/// endpoints cannot diverge (the Gleam Socket.IO endpoint historically lacked
/// the guards beryl_mist gave the Phoenix one). Rates of 0 disable a limit.
/// </summary>
public sealed class TransportGuards(
    ConnectionLimiter connections,
    int messageRate, int messageBurst, int joinRate, int joinBurst,
    TimeProvider time)
{
    public ConnectionLimiter Connections => connections;

    /// <summary>Per-socket inbound-frame budget; over-budget frames are
    /// dropped, matching the Gleam transport.</summary>
    public TokenBucket NewMessageBucket() => new(messageRate, Math.Max(messageBurst, 1), time);

    /// <summary>Per-socket join budget; over-budget joins are dropped.</summary>
    public TokenBucket NewJoinBucket() => new(joinRate, Math.Max(joinBurst, 1), time);
}
