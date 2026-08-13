using Undertow.Abstractions;

namespace Undertow.Storage.Memory;

/// <summary>In-memory IDocumentStore; one lock, matching SQLite's serialized writes.</summary>
public sealed class MemoryDocumentStore : IDocumentStore
{
    private readonly Lock _lock = new();
    private readonly HashSet<string> _documents = [];
    private readonly Dictionary<string, SortedDictionary<long, string>> _ops = [];
    private readonly Dictionary<string, SummaryRecord> _summaries = [];
    private readonly Dictionary<string, CheckpointRecord> _checkpoints = [];

    public ValueTask<bool> HasDocumentAsync(string topic, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return ValueTask.FromResult(_documents.Contains(topic));
        }
    }

    public ValueTask CreateDocumentAsync(string topic, long createdAt, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _documents.Add(topic);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<OpRecord>> GetOpsAsync(
        string topic, long fromExclusive, long? toInclusive, CancellationToken ct = default)
    {
        lock (_lock)
        {
            IReadOnlyList<OpRecord> result = _ops.TryGetValue(topic, out var ops)
                ? ops.Where(kv => kv.Key > fromExclusive && (toInclusive is null || kv.Key <= toInclusive))
                     .Select(kv => new OpRecord(kv.Key, kv.Value))
                     .ToList()
                : [];
            return ValueTask.FromResult(result);
        }
    }

    public ValueTask<long?> GetMaxOpSequenceNumberAsync(string topic, CancellationToken ct = default)
    {
        lock (_lock)
        {
            long? max = _ops.TryGetValue(topic, out var ops) && ops.Count > 0 ? ops.Keys.Max() : null;
            return ValueTask.FromResult(max);
        }
    }

    public ValueTask<SummaryRecord?> GetSummaryAsync(string topic, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return ValueTask.FromResult(_summaries.TryGetValue(topic, out var s) ? s : null);
        }
    }

    public ValueTask PutSummaryAsync(string topic, SummaryRecord summary, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _summaries[topic] = summary;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<CheckpointRecord?> LoadCheckpointAsync(string topic, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return ValueTask.FromResult(_checkpoints.TryGetValue(topic, out var c) ? c : null);
        }
    }

    public ValueTask PruneOpsBelowAsync(string topic, long belowExclusive, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_ops.TryGetValue(topic, out var ops))
            {
                foreach (var sn in ops.Keys.Where(sn => sn < belowExclusive).ToArray())
                    ops.Remove(sn);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> CommitSequencedAsync(
        string topic, OpRecord[] ops, CheckpointRecord next, long expectedVersion,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            var currentVersion = _checkpoints.TryGetValue(topic, out var current) ? current.Version : 0L;
            if (currentVersion != expectedVersion)
                return ValueTask.FromResult(false);

            if (!_ops.TryGetValue(topic, out var topicOps))
                _ops[topic] = topicOps = [];
            foreach (var op in ops)
                topicOps[op.SequenceNumber] = op.Payload;

            _checkpoints[topic] = next;
            return ValueTask.FromResult(true);
        }
    }
}

/// <summary>In-memory IGitObjectStore keyed by tenant (not topic).</summary>
public sealed class MemoryGitObjectStore : IGitObjectStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(string Tenant, string Sha), string> _objects = [];
    private readonly Dictionary<string, SortedDictionary<string, string>> _refs = [];

    public ValueTask<string?> GetObjectAsync(string tenant, string sha, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return ValueTask.FromResult(_objects.TryGetValue((tenant, sha), out var body) ? body : null);
        }
    }

    public ValueTask PutObjectAsync(string tenant, string sha, string body, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _objects[(tenant, sha)] = body;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyDictionary<string, string>> GetObjectsAsync(
        string tenant, IReadOnlyCollection<string> shas, CancellationToken ct = default)
    {
        lock (_lock)
        {
            IReadOnlyDictionary<string, string> result = shas
                .Distinct()
                .Select(sha => (sha, found: _objects.TryGetValue((tenant, sha), out var body), body))
                .Where(x => x.found)
                .ToDictionary(x => x.sha, x => x.body!);
            return ValueTask.FromResult(result);
        }
    }

    public ValueTask<string?> GetRefAsync(string tenant, string path, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var sha = _refs.TryGetValue(tenant, out var refs) && refs.TryGetValue(path, out var s) ? s : null;
            return ValueTask.FromResult(sha);
        }
    }

    public ValueTask<IReadOnlyList<KeyValuePair<string, string>>> ListRefsAsync(
        string tenant, CancellationToken ct = default)
    {
        lock (_lock)
        {
            IReadOnlyList<KeyValuePair<string, string>> result =
                _refs.TryGetValue(tenant, out var refs) ? [.. refs] : [];
            return ValueTask.FromResult(result);
        }
    }

    public ValueTask PutRefAsync(string tenant, string path, string sha, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_refs.TryGetValue(tenant, out var refs))
                _refs[tenant] = refs = [];
            refs[path] = sha;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> TryCreateRefAsync(string tenant, string path, string sha, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_refs.TryGetValue(tenant, out var refs))
                _refs[tenant] = refs = [];
            return ValueTask.FromResult(refs.TryAdd(path, sha));
        }
    }
}
