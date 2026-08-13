namespace Undertow.Abstractions;

/// <summary>One stored op: the payload is the sequenced-op JSON exactly as broadcast.</summary>
public sealed record OpRecord(long SequenceNumber, string Payload);

/// <summary>Sequencer checkpoint. <paramref name="Version"/> is the optimistic-concurrency etag.</summary>
public sealed record CheckpointRecord(
    long SequenceNumber,
    long MinimumSequenceNumber,
    long Version,
    long UpdatedAt);

public sealed record SummaryRecord(string Handle, long SequenceNumber);

/// <summary>
/// Document-scoped storage: documents, ops, summaries, checkpoints. Keyed by
/// document topic (<c>document:{tenant}:{id}</c>) — note the asymmetry with
/// <see cref="IGitObjectStore"/>, which is keyed by tenant.
/// </summary>
public interface IDocumentStore
{
    ValueTask<bool> HasDocumentAsync(string topic, CancellationToken ct = default);
    ValueTask CreateDocumentAsync(string topic, long createdAt, CancellationToken ct = default);

    /// <summary>Ops with sequence number &gt;= <paramref name="fromExclusive"/> + 1, ordered.</summary>
    ValueTask<IReadOnlyList<OpRecord>> GetOpsAsync(
        string topic, long fromExclusive, long? toInclusive, CancellationToken ct = default);

    ValueTask<long?> GetMaxOpSequenceNumberAsync(string topic, CancellationToken ct = default);

    ValueTask<SummaryRecord?> GetSummaryAsync(string topic, CancellationToken ct = default);
    ValueTask PutSummaryAsync(string topic, SummaryRecord summary, CancellationToken ct = default);

    ValueTask<CheckpointRecord?> LoadCheckpointAsync(string topic, CancellationToken ct = default);

    /// <summary>
    /// Single transaction: append ops + advance the checkpoint under optimistic
    /// concurrency. Returns false when <paramref name="expectedVersion"/> no longer
    /// matches (another writer exists). This is the cluster seam.
    /// </summary>
    ValueTask<bool> CommitSequencedAsync(
        string topic, OpRecord[] ops, CheckpointRecord next, long expectedVersion,
        CancellationToken ct = default);

    /// <summary>
    /// Delete stored ops with sequence number &lt; <paramref name="belowExclusive"/>.
    /// Only the flagged post-parity pruning path calls this — it changes what
    /// requestOps and GET /deltas can serve, so it is opt-in.
    /// </summary>
    ValueTask PruneOpsBelowAsync(string topic, long belowExclusive, CancellationToken ct = default);
}

/// <summary>Git-like object storage keyed by tenant (not topic).</summary>
public interface IGitObjectStore
{
    ValueTask<string?> GetObjectAsync(string tenant, string sha, CancellationToken ct = default);
    ValueTask PutObjectAsync(string tenant, string sha, string body, CancellationToken ct = default);
    ValueTask<IReadOnlyDictionary<string, string>> GetObjectsAsync(
        string tenant, IReadOnlyCollection<string> shas, CancellationToken ct = default);

    ValueTask<string?> GetRefAsync(string tenant, string path, CancellationToken ct = default);
    ValueTask<IReadOnlyList<KeyValuePair<string, string>>> ListRefsAsync(
        string tenant, CancellationToken ct = default);
    ValueTask PutRefAsync(string tenant, string path, string sha, CancellationToken ct = default);

    /// <summary>Compare-and-set create: false when the ref already exists.</summary>
    ValueTask<bool> TryCreateRefAsync(string tenant, string path, string sha, CancellationToken ct = default);
}
