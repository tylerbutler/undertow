using Undertow.Abstractions;
using Undertow.Protocol;

namespace Undertow.Server;

/// <summary>
/// Pre-loads the object closures the pure Silt response shapes need, so the F#
/// tier's fetch callback is a dictionary lookup — O(depth) batched round-trips
/// instead of N+1 queries.
/// </summary>
public static class Historian
{
    /// <summary>Transitive closure of tree children under a root tree body.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> LoadTreeClosureAsync(
        IGitObjectStore store, string tenant, string rootBody, CancellationToken ct = default)
    {
        var loaded = new Dictionary<string, string>();
        var frontier = SiltBoundary.treeChildShas(rootBody);

        // Depth cap mirrors silt's flatten_tree recursion cap.
        for (var depth = 0; depth < 64 && frontier.Length > 0; depth++)
        {
            var missing = frontier.Where(sha => !loaded.ContainsKey(sha)).ToArray();
            if (missing.Length == 0)
                break;

            var fetched = await store.GetObjectsAsync(tenant, missing, ct);
            foreach (var (sha, body) in fetched)
                loaded[sha] = body;

            frontier = fetched.Values.SelectMany(SiltBoundary.treeChildShas).ToArray();
        }

        return loaded;
    }

    /// <summary>First-parent commit chain from <paramref name="sha"/>, up to <paramref name="count"/>.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> LoadCommitChainAsync(
        IGitObjectStore store, string tenant, string sha, int count, CancellationToken ct = default)
    {
        var loaded = new Dictionary<string, string>();
        var current = sha;
        for (var i = 0; i < count && current is not null && !loaded.ContainsKey(current); i++)
        {
            var body = await store.GetObjectAsync(tenant, current, ct);
            if (body is null)
                break;
            loaded[current] = body;
            current = SiltBoundary.commitFirstParent(body);
        }

        return loaded;
    }
}
