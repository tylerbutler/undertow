using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Undertow.Abstractions;
using Undertow.Protocol;
using Undertow.Runtime;
using Undertow.Storage.Memory;

namespace Undertow.Server.Tests;

/// <summary>
/// The Phase-8 guard tests the conformance suites cannot cover: the liveness
/// sweep (a stale RSN must not pin MSN), idle-document eviction, ceilings,
/// signal targeting, and per-document isolation — all against the real
/// DocumentChannel with FakeTimeProvider.
/// </summary>
public class RuntimeGuardTests
{
    private const string Tenant = "fluid";
    private const string Secret = "guard-secret";
    private const string Doc = "guard-doc";
    private const string Topic = $"document:{Tenant}:{Doc}";

    private sealed record Harness(
        FakeTimeProvider Time,
        MemoryDocumentStore Store,
        DocumentRegistry Documents,
        SocketRegistry Sockets,
        ChannelDispatcher Dispatcher);

    private static Harness NewHarness()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_786_000_000));
        var store = new MemoryDocumentStore();
        var git = new MemoryGitObjectStore();
        var documents = new DocumentRegistry(store, git, time, compatRestoreMsnFromSummary: false);
        var sockets = new SocketRegistry();
        var broadcaster = new LocalBroadcaster(sockets);
        var handler = new DocumentChannel(
            documents, store, git, Tenant, Secret, maxFrameBytes: 16_777_216, time);
        var dispatcher = new ChannelDispatcher(sockets, broadcaster, handler);
        return new Harness(time, store, documents, sockets, dispatcher);
    }

    private static string MintToken(FakeTimeProvider time, string doc = Doc) =>
        AuthBoundary.mintToken(
            Tenant, doc, ["doc:read", "doc:write", "summary:write"], "guard-user", Secret,
            time.GetUtcNow().ToUnixTimeSeconds(), 3600, "guard-jti");

    /// <summary>Join + connect a write-mode client over a fake socket.</summary>
    private static async Task<(SocketConnection Connection, FakeWebSocket Socket)> ConnectWriteAsync(
        Harness harness, string doc = Doc)
    {
        var socket = new FakeWebSocket();
        var connection = new SocketConnection(
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)),
            socket, PhoenixTestFraming.Instance, harness.Time);
        harness.Sockets.Register(connection);

        var topic = $"document:{Tenant}:{doc}";
        var token = MintToken(harness.Time, doc);
        using var join = JsonDocument.Parse($$"""{"token":"{{token}}"}""");
        var joined = await harness.Dispatcher.JoinAsync(connection, "1", topic, join.RootElement.Clone());
        Assert.True(joined.Ok);

        using var connect = JsonDocument.Parse(
            "{\"tenantId\":\"" + Tenant + "\",\"id\":\"" + doc + "\",\"token\":\"" + token +
            "\",\"mode\":\"write\",\"client\":{\"mode\":\"write\",\"details\":{\"capabilities\":" +
            "{\"interactive\":true}},\"permission\":[],\"scopes\":[],\"user\":{\"id\":\"guard-user\"}}}");
        var outcome = await harness.Dispatcher.HandleEventAsync(
            connection, topic, "connect_document", connect.RootElement.Clone());
        Assert.Equal("connect_document_success", outcome.PushEvent);
        return (connection, socket);
    }

    [Fact]
    public async Task Sweep_EvictsSilentSocket_EmitsLeave_AdvancesMsn()
    {
        var harness = NewHarness();
        var sweeper = new SocketSweeper(harness.Sockets, harness.Dispatcher, harness.Time, timeoutMs: 60_000);

        var (stale, _) = await ConnectWriteAsync(harness);
        harness.Time.Advance(TimeSpan.FromSeconds(45));
        var (live, liveSocket) = await ConnectWriteAsync(harness);

        // The stale client's RSN (0) pins the MSN.
        var pinned = await harness.Store.LoadCheckpointAsync(Topic);
        Assert.NotNull(pinned);
        Assert.Equal(0, pinned.MinimumSequenceNumber);

        // Advance past the 60 s tolerance for the stale socket only; the live
        // one just connected (its join refreshed nothing — set its clock now).
        Volatile.Write(ref live.LastInboundTimestamp, harness.Time.GetTimestamp());
        harness.Time.Advance(TimeSpan.FromSeconds(20));
        await sweeper.SweepOnceAsync();

        // Stale socket evicted and actively closed; live one survives.
        Assert.Null(harness.Sockets.Get(stale.Id));
        Assert.NotNull(harness.Sockets.Get(live.Id));

        // The eviction emitted the sequenced leave op...
        var ops = await harness.Store.GetOpsAsync(Topic, 0, null);
        Assert.Contains(ops, op => op.Payload.Contains("\"type\":\"leave\""));

        // ...and the MSN is no longer pinned by the stale RSN. That advance is
        // the actual bug being prevented.
        var after = await harness.Store.LoadCheckpointAsync(Topic);
        Assert.NotNull(after);
        Assert.True(after.MinimumSequenceNumber > pinned.MinimumSequenceNumber,
            $"MSN did not advance: {after.MinimumSequenceNumber}");

        // The survivor observed the leave.
        Assert.Contains(liveSocket.SentFrames, f => f.Contains("\"type\":\"leave\""));
    }

    [Fact]
    public async Task Sweep_SocketWithRecentInbound_Survives()
    {
        var harness = NewHarness();
        var sweeper = new SocketSweeper(harness.Sockets, harness.Dispatcher, harness.Time, timeoutMs: 60_000);
        var (connection, _) = await ConnectWriteAsync(harness);

        harness.Time.Advance(TimeSpan.FromSeconds(50));
        Volatile.Write(ref connection.LastInboundTimestamp, harness.Time.GetTimestamp());
        harness.Time.Advance(TimeSpan.FromSeconds(30));
        await sweeper.SweepOnceAsync();

        Assert.NotNull(harness.Sockets.Get(connection.Id));
    }

    [Fact]
    public async Task IdleEviction_DropsClientlessDocument_KeepsNumbering()
    {
        var harness = NewHarness();
        var idle = new DocumentIdleSweeper(harness.Documents, harness.Time, idleMs: 300_000);

        // Connect + disconnect so the doc has ops but no clients.
        var (connection, _) = await ConnectWriteAsync(harness);
        await harness.Dispatcher.TerminateAllAsync(connection, sendClose: false);
        harness.Sockets.Unregister(connection.Id);

        var before = await harness.Documents.GetOrCreateAsync(Topic);
        var snBefore = await before.SequenceNumberAsync();
        Assert.True(snBefore >= 2); // join + leave

        harness.Time.Advance(TimeSpan.FromMinutes(6));
        await idle.SweepOnceAsync();
        Assert.Null(harness.Documents.TryGet(Topic));

        // Rehydration keeps the sequence numbering, with an empty roster.
        var rehydrated = await harness.Documents.GetOrCreateAsync(Topic);
        Assert.Equal(snBefore, await rehydrated.SequenceNumberAsync());
        Assert.Empty(await rehydrated.ClientsAsync());
    }

    [Fact]
    public async Task IdleEviction_NeverDropsDocumentWithClients()
    {
        var harness = NewHarness();
        var idle = new DocumentIdleSweeper(harness.Documents, harness.Time, idleMs: 300_000);
        await ConnectWriteAsync(harness);

        harness.Time.Advance(TimeSpan.FromHours(2));
        await idle.SweepOnceAsync();
        Assert.NotNull(harness.Documents.TryGet(Topic));
    }

    [Fact]
    public void ConnectionLimiter_RefusesNPlusOneFromOneAddress()
    {
        var limiter = new ConnectionLimiter(maxPerIp: 2, maxTotal: 0);
        Assert.True(limiter.TryAcquire("1.2.3.4"));
        Assert.True(limiter.TryAcquire("1.2.3.4"));
        Assert.False(limiter.TryAcquire("1.2.3.4"));
        Assert.True(limiter.TryAcquire("5.6.7.8"));

        limiter.Release("1.2.3.4");
        Assert.True(limiter.TryAcquire("1.2.3.4"));
    }

    [Fact]
    public void ConnectionLimiter_ZeroMeansUnlimited()
    {
        var limiter = new ConnectionLimiter(maxPerIp: 0, maxTotal: 0);
        for (var i = 0; i < 10_000; i++)
            Assert.True(limiter.TryAcquire("1.2.3.4"));
    }

    [Fact]
    public void TokenBucket_RefillsFromTimeProvider()
    {
        var time = new FakeTimeProvider();
        var bucket = new TokenBucket(ratePerSecond: 10, burst: 2, time);
        Assert.True(bucket.TryTake());
        Assert.True(bucket.TryTake());
        Assert.False(bucket.TryTake());

        time.Advance(TimeSpan.FromMilliseconds(100)); // one token
        Assert.True(bucket.TryTake());
        Assert.False(bucket.TryTake());
    }

    [Fact]
    public void TokenBucket_ZeroRateIsUnlimited()
    {
        var bucket = new TokenBucket(0, 0, new FakeTimeProvider());
        for (var i = 0; i < 10_000; i++)
            Assert.True(bucket.TryTake());
    }

    [Fact]
    public async Task SignalTargeting_ReachesExactlyTheNamedClient()
    {
        // The Fluid clientId IS the socket id, so targeting b.Id addresses
        // exactly the second connection.
        var harness = NewHarness();
        var (a, socketA) = await ConnectWriteAsync(harness);
        var (b, socketB) = await ConnectWriteAsync(harness);
        var (_, socketC) = await ConnectWriteAsync(harness);
        await Task.Delay(100);
        var framesBeforeB = socketB.SentFrames.Count;
        var framesBeforeC = socketC.SentFrames.Count;
        var framesBeforeA = socketA.SentFrames.Count;

        using var payload = JsonDocument.Parse(
            $$"""{"clientId":"{{a.Id}}","contentBatches":[{"targetClientId":"{{b.Id}}","content":{"probe":1}""" + "}]}");
        await harness.Dispatcher.HandleEventAsync(a, Topic, "submitSignal", payload.RootElement.Clone());
        await Task.Delay(100);

        Assert.Contains(socketB.SentFrames.Skip(framesBeforeB), f => f.Contains("probe"));
        Assert.DoesNotContain(socketC.SentFrames.Skip(framesBeforeC), f => f.Contains("probe"));
        Assert.DoesNotContain(socketA.SentFrames.Skip(framesBeforeA), f => f.Contains("probe"));

        // A signal naming an unknown client reaches nobody (the targeted list
        // is intersected against the known client ids).
        using var ghost = JsonDocument.Parse(
            $$"""{"clientId":"{{a.Id}}","contentBatches":[{"targetClientId":"GHOST","content":{"ghostprobe":1}""" + "}]}");
        await harness.Dispatcher.HandleEventAsync(a, Topic, "submitSignal", ghost.RootElement.Clone());
        await Task.Delay(100);
        foreach (var socket in new[] { socketA, socketB, socketC })
            Assert.DoesNotContain(socket.SentFrames, f => f.Contains("ghostprobe"));
    }

    [Fact]
    public async Task OpPruning_Flagged_PrunesBelowSummaryAfterSummaryCommit()
    {
        // Post-parity, default off: with UNDERTOW_OP_PRUNE_BELOW_SUMMARY=1,
        // a committed summary prunes stored ops below its sequence number.
        var time = new FakeTimeProvider();
        var store = new MemoryDocumentStore();
        var git = new MemoryGitObjectStore();
        var registry = new DocumentRegistry(
            store, git, time, compatRestoreMsnFromSummary: false, pruneOpsBelowSummary: true);
        var session = await registry.GetOrCreateAsync("document:fluid:prune-doc");

        await session.ConnectAsync("c1", "write", "{}", "{}", 0);
        for (var csn = 1; csn <= 3; csn++)
        {
            await session.SubmitMessageAsync(
                "c1", csn, 0, (sn, msn) => $$"""{"sequenceNumber":{{sn}},"n":{{sn}}}""");
        }

        var summary = await session.SubmitSummaryMessagesAsync(
            "c1", 4, 0, (summarySn, responseSn, msn) =>
                ($$"""{"sequenceNumber":{{summarySn}},"type":"summarize"}""",
                 $$"""{"sequenceNumber":{{responseSn}},"type":"summaryAck"}""",
                 "summary-sha"));
        Assert.True(summary.Assigned);

        var remaining = await store.GetOpsAsync("document:fluid:prune-doc", 0, null);
        Assert.All(remaining, op => Assert.True(op.SequenceNumber >= summary.SummarySn));
        Assert.Contains(remaining, op => op.SequenceNumber == summary.ResponseSn);
    }

    [Fact]
    public async Task DocumentIsolation_SlowWorkOnOneDocumentDoesNotDelayAnother()
    {
        var harness = NewHarness();
        var slow = await harness.Documents.GetOrCreateAsync("document:fluid:slow-doc");
        var fast = await harness.Documents.GetOrCreateAsync("document:fluid:fast-doc");

        await slow.ConnectAsync("slow-client", "write", "{}", "{}", 0);
        await fast.ConnectAsync("fast-client", "write", "{}", "{}", 0);

        var gate = new ManualResetEventSlim(false);
        var slowTask = Task.Run(async () => await slow.SubmitMessageAsync(
            "slow-client", 1, 0,
            build: (sn, msn) =>
            {
                gate.Wait(TimeSpan.FromSeconds(5)); // slow build inside the lock
                return $$"""{"sequenceNumber":{{sn}},"slow":true}""";
            }));

        // The fast document's submit completes well inside the slow one's window.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await fast.SubmitMessageAsync(
            "fast-client", 1, 0, build: (sn, msn) => $$"""{"sequenceNumber":{{sn}},"fast":true}""");
        stopwatch.Stop();
        Assert.True(result.Assigned);
        Assert.True(stopwatch.ElapsedMilliseconds < 1000, $"fast submit took {stopwatch.ElapsedMilliseconds}ms");

        // The slow work really did run and commit — without this the test
        // passes even if the slow document never processed anything.
        gate.Set();
        var slowResult = await slowTask;
        Assert.True(slowResult.Assigned);
        var slowOps = await harness.Store.GetOpsAsync("document:fluid:slow-doc", 0, null);
        Assert.Contains(slowOps, op => op.Payload.Contains("\"slow\":true"));
    }
}

/// <summary>Minimal framing for fake-socket tests (Phoenix push shape).</summary>
file sealed class PhoenixTestFraming : ISocketFraming
{
    public static readonly PhoenixTestFraming Instance = new();

    public ReadOnlyMemory<byte> Push(string topic, string @event, ReadOnlyMemory<byte> payload) =>
        Undertow.Transports.PhoenixFraming.Encode(null, null, topic, @event, payload);

    public ReadOnlyMemory<byte>? Close(string topic, string joinRef) =>
        Undertow.Transports.PhoenixFraming.Encode(joinRef, joinRef, topic, "phx_close", "{}"u8.ToArray());
}
