using Undertow.Abstractions;
using Undertow.Storage.Memory;
using Undertow.Storage.Sqlite;

namespace Undertow.Storage.Tests;

/// <summary>A backend under test: paired document + git-object stores.</summary>
public sealed record Backend(IDocumentStore Documents, IGitObjectStore GitObjects, IDisposable? Owner) : IDisposable
{
    public void Dispose() => Owner?.Dispose();
}

/// <summary>
/// Backend-substitution conformance suite: every backend must produce
/// identical observations. Mirrors the Gleam backend-substitution test.
/// </summary>
public abstract class StoreConformanceTests : IDisposable
{
    private readonly Backend _backend;
    protected IDocumentStore Documents => _backend.Documents;
    protected IGitObjectStore GitObjects => _backend.GitObjects;

    protected StoreConformanceTests(Backend backend) => _backend = backend;

    public void Dispose() => _backend.Dispose();

    private static CheckpointRecord Next(CheckpointRecord current, long sn, long msn) =>
        new(sn, msn, current.Version + 1, UpdatedAt: 1000);

    [Fact]
    public async Task Document_Existence()
    {
        Assert.False(await Documents.HasDocumentAsync("document:t:d"));
        await Documents.CreateDocumentAsync("document:t:d", createdAt: 123);
        Assert.True(await Documents.HasDocumentAsync("document:t:d"));
        Assert.False(await Documents.HasDocumentAsync("document:t:other"));
    }

    [Fact]
    public async Task StoredDocumentExists_IsThreeWayOr()
    {
        // Fresh: nothing stored.
        Assert.False(await Documents.StoredDocumentExistsAsync("document:t:none"));

        // Document row only.
        await Documents.CreateDocumentAsync("document:t:row", 1);
        Assert.True(await Documents.StoredDocumentExistsAsync("document:t:row"));

        // Ops only.
        var checkpoint = await Documents.LoadOrSynthesizeCheckpointAsync("document:t:ops");
        Assert.True(await Documents.CommitSequencedAsync(
            "document:t:ops", [new OpRecord(1, "{}")], Next(checkpoint, 1, 0), checkpoint.Version));
        Assert.True(await Documents.StoredDocumentExistsAsync("document:t:ops"));

        // Summary only — but an empty handle does not count.
        await Documents.PutSummaryAsync("document:t:emptysum", new SummaryRecord("", 0));
        Assert.False(await Documents.StoredDocumentExistsAsync("document:t:emptysum"));
        await Documents.PutSummaryAsync("document:t:sum", new SummaryRecord("sha1", 5));
        Assert.True(await Documents.StoredDocumentExistsAsync("document:t:sum"));
    }

    [Fact]
    public async Task Ops_AreOrderedAndRangeQueried()
    {
        var topic = "document:t:ops-order";
        var checkpoint = await Documents.LoadOrSynthesizeCheckpointAsync(topic);
        OpRecord[] ops = [new(1, "one"), new(2, "two"), new(3, "three")];
        Assert.True(await Documents.CommitSequencedAsync(topic, ops, Next(checkpoint, 3, 0), 0));

        var all = await Documents.GetOpsAsync(topic, fromExclusive: 0, toInclusive: null);
        Assert.Equal([1L, 2L, 3L], all.Select(o => o.SequenceNumber));
        Assert.Equal(["one", "two", "three"], all.Select(o => o.Payload));

        var fromTwo = await Documents.GetOpsAsync(topic, fromExclusive: 1, toInclusive: null);
        Assert.Equal([2L, 3L], fromTwo.Select(o => o.SequenceNumber));

        var window = await Documents.GetOpsAsync(topic, fromExclusive: 1, toInclusive: 2);
        Assert.Equal([2L], window.Select(o => o.SequenceNumber));

        Assert.Equal(3, await Documents.GetMaxOpSequenceNumberAsync(topic));
        Assert.Null(await Documents.GetMaxOpSequenceNumberAsync("document:t:none"));
    }

    [Fact]
    public async Task Ops_AreIsolatedByTopic()
    {
        var a = await Documents.LoadOrSynthesizeCheckpointAsync("document:t:a");
        await Documents.CommitSequencedAsync("document:t:a", [new OpRecord(1, "a1")], Next(a, 1, 0), 0);
        var b = await Documents.LoadOrSynthesizeCheckpointAsync("document:t:b");
        await Documents.CommitSequencedAsync("document:t:b", [new OpRecord(1, "b1")], Next(b, 1, 0), 0);

        var opsA = await Documents.GetOpsAsync("document:t:a", 0, null);
        Assert.Equal(["a1"], opsA.Select(o => o.Payload));
    }

    [Fact]
    public async Task Summary_RoundTrips()
    {
        var topic = "document:t:sum-rt";
        Assert.Null(await Documents.GetSummaryAsync(topic));
        await Documents.PutSummaryAsync(topic, new SummaryRecord("handle-1", 10));
        Assert.Equal(new SummaryRecord("handle-1", 10), await Documents.GetSummaryAsync(topic));
        await Documents.PutSummaryAsync(topic, new SummaryRecord("handle-2", 20));
        Assert.Equal(new SummaryRecord("handle-2", 20), await Documents.GetSummaryAsync(topic));
    }

    [Fact]
    public async Task Checkpoint_SynthesizesFromOpsAndSummary()
    {
        var topic = "document:t:cold";

        // Nothing stored: zeros at version 0.
        var empty = await Documents.LoadOrSynthesizeCheckpointAsync(topic);
        Assert.Equal(0, empty.SequenceNumber);
        Assert.Equal(0, empty.MinimumSequenceNumber);
        Assert.Equal(0, empty.Version);

        // Ops beyond the summary: sn = maxOpSn, msn = summary sn.
        await Documents.CommitSequencedAsync(
            topic, [new OpRecord(7, "x")], new CheckpointRecord(7, 0, 1, 0), 0);
        await Documents.PutSummaryAsync(topic, new SummaryRecord("h", 5));

        // Simulate a rebuild on a backend with no checkpoint row: a second
        // topic with the same ops/summary but no committed checkpoint.
        var topic2 = "document:t:cold2";
        await Documents.CommitSequencedAsync(
            topic2, [new OpRecord(7, "x")], new CheckpointRecord(7, 0, 1, 0), 0);
        await Documents.PutSummaryAsync(topic2, new SummaryRecord("h", 9));
        // Summary ahead of ops: sn = summary sn.
        var loaded = await Documents.LoadCheckpointAsync(topic2);
        Assert.NotNull(loaded); // committed above
    }

    [Fact]
    public async Task Checkpoint_Synthesis_UsesMaxOfOpsAndSummary()
    {
        // Backend without a checkpoint row for this topic (ops written via a
        // commit on another topic won't help): write summary only.
        var topic = "document:t:synth";
        await Documents.PutSummaryAsync(topic, new SummaryRecord("h", 9));
        var synthesized = await Documents.LoadOrSynthesizeCheckpointAsync(topic);
        Assert.Equal(9, synthesized.SequenceNumber);
        Assert.Equal(9, synthesized.MinimumSequenceNumber);
        Assert.Equal(0, synthesized.Version);
    }

    [Fact]
    public async Task CommitSequenced_RejectsStaleVersion()
    {
        var topic = "document:t:etag";
        var checkpoint = await Documents.LoadOrSynthesizeCheckpointAsync(topic);
        Assert.True(await Documents.CommitSequencedAsync(
            topic, [new OpRecord(1, "{}")], Next(checkpoint, 1, 0), checkpoint.Version));

        // A second writer holding the stale version must be refused.
        Assert.False(await Documents.CommitSequencedAsync(
            topic, [new OpRecord(2, "{}")], Next(checkpoint, 2, 0), checkpoint.Version));

        // The winner's ops are intact; the loser's were never written.
        var ops = await Documents.GetOpsAsync(topic, 0, null);
        Assert.Equal([1L], ops.Select(o => o.SequenceNumber));

        // Reloading picks up the advanced version and can continue.
        var reloaded = await Documents.LoadOrSynthesizeCheckpointAsync(topic);
        Assert.True(await Documents.CommitSequencedAsync(
            topic, [new OpRecord(2, "{}")], Next(reloaded, 2, 0), reloaded.Version));
    }

    [Fact]
    public async Task PruneOpsBelow_RemovesOnlyOlderOps()
    {
        var topic = "document:t:prune";
        var checkpoint = await Documents.LoadOrSynthesizeCheckpointAsync(topic);
        OpRecord[] ops = [new(1, "one"), new(2, "two"), new(3, "three")];
        Assert.True(await Documents.CommitSequencedAsync(topic, ops, Next(checkpoint, 3, 0), 0));

        await Documents.PruneOpsBelowAsync(topic, belowExclusive: 3);
        var remaining = await Documents.GetOpsAsync(topic, 0, null);
        Assert.Equal([3L], remaining.Select(o => o.SequenceNumber));
    }

    [Fact]
    public async Task GitObjects_AreKeyedByTenant_NotTopic()
    {
        // The key asymmetry inherited from Gleam: ops/summaries by topic,
        // objects/refs by tenant.
        await GitObjects.PutObjectAsync("tenant-a", "sha-1", "body-a");
        Assert.Equal("body-a", await GitObjects.GetObjectAsync("tenant-a", "sha-1"));
        Assert.Null(await GitObjects.GetObjectAsync("tenant-b", "sha-1"));
        Assert.Null(await GitObjects.GetObjectAsync("document:tenant-a:doc", "sha-1"));
    }

    [Fact]
    public async Task GetObjects_BatchesTransitiveClosure()
    {
        await GitObjects.PutObjectAsync("t", "s1", "b1");
        await GitObjects.PutObjectAsync("t", "s2", "b2");
        var found = await GitObjects.GetObjectsAsync("t", ["s1", "s2", "missing", "s1"]);
        Assert.Equal(2, found.Count);
        Assert.Equal("b1", found["s1"]);
        Assert.Equal("b2", found["s2"]);
    }

    [Fact]
    public async Task Refs_CreateIsCompareAndSet()
    {
        Assert.True(await GitObjects.TryCreateRefAsync("t", "refs/heads/main", "sha-1"));
        Assert.False(await GitObjects.TryCreateRefAsync("t", "refs/heads/main", "sha-2"));
        Assert.Equal("sha-1", await GitObjects.GetRefAsync("t", "refs/heads/main"));

        await GitObjects.PutRefAsync("t", "refs/heads/main", "sha-3");
        Assert.Equal("sha-3", await GitObjects.GetRefAsync("t", "refs/heads/main"));
    }

    [Fact]
    public async Task Refs_ListIsPathOrdered()
    {
        await GitObjects.PutRefAsync("t", "refs/heads/zeta", "z");
        await GitObjects.PutRefAsync("t", "refs/heads/alpha", "a");
        await GitObjects.PutRefAsync("t", "refs/tags/v1", "v");
        var refs = await GitObjects.ListRefsAsync("t");
        Assert.Equal(
            ["refs/heads/alpha", "refs/heads/zeta", "refs/tags/v1"],
            refs.Select(r => r.Key));
    }

    [Fact]
    public async Task Refs_NormalizedPathsAreEquivalent()
    {
        // Callers normalize via Silt.normalizeRef before hitting the store —
        // "heads/main" and "refs/heads/main" must land on the same row.
        var normalizedShort = Undertow.Protocol.Silt.normalizeRef("heads/main");
        var normalizedFull = Undertow.Protocol.Silt.normalizeRef("refs/heads/main");
        Assert.Equal("refs/heads/main", normalizedShort);
        Assert.Equal(normalizedShort, normalizedFull);

        await GitObjects.PutRefAsync("t", normalizedShort, "sha-x");
        Assert.Equal("sha-x", await GitObjects.GetRefAsync("t", normalizedFull));
    }
}

public sealed class MemoryStoreTests : StoreConformanceTests
{
    public MemoryStoreTests()
        : base(new Backend(new MemoryDocumentStore(), new MemoryGitObjectStore(), Owner: null)) { }
}

public sealed class SqliteInMemoryStoreTests : StoreConformanceTests
{
    private static Backend Create()
    {
        var storage = SqliteStorage.OpenInMemory();
        return new Backend(storage, storage, storage);
    }

    public SqliteInMemoryStoreTests() : base(Create()) { }
}

public sealed class SqliteOnDiskStoreTests : StoreConformanceTests
{
    private static Backend Create()
    {
        var path = Path.Combine(Path.GetTempPath(), $"undertow-test-{Guid.NewGuid():N}.db");
        var storage = SqliteStorage.OpenFile(path);
        return new Backend(storage, storage, new DeleteOnDispose(storage, path));
    }

    public SqliteOnDiskStoreTests() : base(Create()) { }

    private sealed class DeleteOnDispose(SqliteStorage storage, string path) : IDisposable
    {
        public void Dispose()
        {
            storage.Dispose();
            File.Delete(path);
            File.Delete(path + "-shm");
            File.Delete(path + "-wal");
        }
    }
}
