using System.Net.WebSockets;

namespace Undertow.Runtime;

/// <summary>
/// The coordinator liveness sweep: every timeout/2, evict any socket whose
/// last *inbound frame* (any frame, not just heartbeats) is older than the
/// timeout. This is load-bearing for correctness, not hygiene: a half-open
/// socket stays in the session roster and its stale RSN pins the document's
/// MSN, blocking summarization for everyone else. Eviction is the same path
/// as a clean disconnect — the write-mode leave op is emitted — and then the
/// socket is actually closed (the register_closer equivalent), so no zombie
/// remains.
/// </summary>
public sealed class SocketSweeper(
    SocketRegistry registry, ChannelDispatcher dispatcher, TimeProvider time, int timeoutMs)
{
    public int CheckIntervalMs => Math.Max(1, timeoutMs / 2);

    public async Task RunAsync(CancellationToken ct)
    {
        if (timeoutMs <= 0)
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(CheckIntervalMs), time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await SweepOnceAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>One sweep pass, exposed for deterministic tests.</summary>
    public async Task SweepOnceAsync()
    {
        foreach (var connection in registry.All())
        {
            var silent = time.GetElapsedTime(Volatile.Read(ref connection.LastInboundTimestamp));
            if (silent.TotalMilliseconds <= timeoutMs)
                continue;

            await dispatcher.TerminateAllAsync(connection, sendClose: true);
            registry.Unregister(connection.Id);
            connection.Abort(WebSocketCloseStatus.EndpointUnavailable, "heartbeat timeout");
        }
    }
}

/// <summary>
/// Idle-document eviction: a session with no connected clients is dropped
/// after the idle window (checked at half the window); a document with a
/// connected client is never dropped, however idle. Sequence numbering
/// survives eviction — rehydration is a pure function of storage.
/// </summary>
public sealed class DocumentIdleSweeper(DocumentRegistry documents, TimeProvider time, long idleMs)
{
    public long CheckIntervalMs => Math.Max(1, idleMs / 2);

    public async Task RunAsync(CancellationToken ct)
    {
        if (idleMs <= 0)
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(CheckIntervalMs), time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await SweepOnceAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task SweepOnceAsync()
    {
        foreach (var topic in documents.LiveTopics())
        {
            if (documents.TryGet(topic) is not { } session)
                continue;

            if (await session.PresenceCountAsync() > 0)
                continue;

            if (time.GetElapsedTime(session.LastTouchedMs).TotalMilliseconds > idleMs)
                documents.TryEvict(topic);
        }
    }
}
