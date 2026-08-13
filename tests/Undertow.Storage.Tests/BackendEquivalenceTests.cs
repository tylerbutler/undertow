using Undertow.Abstractions;
using Undertow.Storage.Memory;
using Undertow.Storage.Sqlite;

namespace Undertow.Storage.Tests;

/// <summary>
/// Replays seeded pseudo-random operation sequences against every backend and
/// asserts identical observations — the strengthened form of the Gleam
/// "two backends produce identical observations" property.
/// </summary>
public class BackendEquivalenceTests
{
    private sealed record Observation(
        bool Exists,
        long? MaxOpSn,
        string OpsJoined,
        SummaryRecord? Summary,
        string RefsJoined,
        string ObjectsJoined);

    private static async Task<Observation> Observe(IDocumentStore docs, IGitObjectStore git, string topic, string tenant)
    {
        var ops = await docs.GetOpsAsync(topic, 0, null);
        var refs = await git.ListRefsAsync(tenant);
        var objects = new List<string>();
        foreach (var sha in Enumerable.Range(0, 10).Select(i => $"sha-{i}"))
        {
            if (await git.GetObjectAsync(tenant, sha) is { } body)
                objects.Add($"{sha}={body}");
        }

        return new Observation(
            await docs.StoredDocumentExistsAsync(topic),
            await docs.GetMaxOpSequenceNumberAsync(topic),
            string.Join("|", ops.Select(o => $"{o.SequenceNumber}:{o.Payload}")),
            await docs.GetSummaryAsync(topic),
            string.Join("|", refs.Select(r => $"{r.Key}={r.Value}")),
            string.Join("|", objects));
    }

    private static async Task Replay(IDocumentStore docs, IGitObjectStore git, int seed, int steps)
    {
        var random = new Random(seed);
        var topic = "document:t:replay";
        var tenant = "t";
        var nextSn = 1L;

        for (var i = 0; i < steps; i++)
        {
            switch (random.Next(6))
            {
                case 0:
                    await docs.CreateDocumentAsync(topic, random.Next(1000));
                    break;
                case 1:
                    var checkpoint = await docs.LoadOrSynthesizeCheckpointAsync(topic);
                    var count = random.Next(1, 4);
                    var ops = Enumerable.Range(0, count)
                        .Select(j => new OpRecord(nextSn + j, $"op-{nextSn + j}-{random.Next(100)}"))
                        .ToArray();
                    var committed = await docs.CommitSequencedAsync(
                        topic, ops,
                        new CheckpointRecord(nextSn + count - 1, 0, checkpoint.Version + 1, 1000),
                        checkpoint.Version);
                    if (committed)
                        nextSn += count;
                    break;
                case 2:
                    await docs.PutSummaryAsync(topic, new SummaryRecord($"handle-{random.Next(100)}", random.Next(50)));
                    break;
                case 3:
                    await git.PutObjectAsync(tenant, $"sha-{random.Next(10)}", $"body-{random.Next(100)}");
                    break;
                case 4:
                    await git.PutRefAsync(tenant, $"refs/heads/{random.Next(5)}", $"ref-sha-{random.Next(100)}");
                    break;
                case 5:
                    await git.TryCreateRefAsync(tenant, $"refs/tags/{random.Next(5)}", $"tag-sha-{random.Next(100)}");
                    break;
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(2026)]
    public async Task AllBackends_ProduceIdenticalObservations(int seed)
    {
        var memoryDocs = new MemoryDocumentStore();
        var memoryGit = new MemoryGitObjectStore();
        using var sqlite = SqliteStorage.OpenInMemory();

        await Replay(memoryDocs, memoryGit, seed, steps: 60);
        await Replay(sqlite, sqlite, seed, steps: 60);

        var memoryView = await Observe(memoryDocs, memoryGit, "document:t:replay", "t");
        var sqliteView = await Observe(sqlite, sqlite, "document:t:replay", "t");
        Assert.Equal(memoryView, sqliteView);
    }
}
