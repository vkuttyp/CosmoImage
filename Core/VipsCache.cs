using System.Collections.Generic;

namespace CosmoImage.Core;

/// <summary>
/// Operation result cache. Hash-keyed (<see cref="VipsOperation.GetCacheKey"/>)
/// shared lookup across all <c>Run</c> calls — when two callers build the
/// same op with the same inputs, the second one returns the first's
/// <see cref="VipsImage"/> result without re-running <c>Build</c>.
///
/// <para>LRU + cost-based eviction. Cost is an estimate of the
/// materialised pixel-buffer size (<c>W·H·SizeOfPel</c>); when the
/// total cost exceeds <see cref="MaxCost"/>, the least-recently-used
/// entries get dropped from the tail of the LRU list until the cache
/// is back under the cap. Successful <see cref="Get"/> moves an entry
/// to the head (most-recent), keeping it from premature eviction.</para>
///
/// <para>Default cap is 256 MiB — large enough to absorb a typical
/// thumbnail-pipeline DAG without blowing memory on long batch jobs.
/// Tune via <see cref="SetMaxCost"/>; pass <c>0</c> to disable
/// caching entirely.</para>
/// </summary>
public static class VipsCache
{
    private sealed class Entry
    {
        public int Key;
        public VipsImage Image = null!;
        public long Cost;
    }

    private static readonly Dictionary<int, LinkedListNode<Entry>> _map = new();
    private static readonly LinkedList<Entry> _lru = new();
    private static long _totalCost;
    private static long _maxCost = 256L * 1024 * 1024;
    private static readonly object _lock = new();

    /// <summary>Current cap on total cached cost (estimated pixel bytes).</summary>
    public static long MaxCost
    {
        get { lock (_lock) return _maxCost; }
    }

    /// <summary>Resize the cap. Existing entries past the new cap evict immediately (oldest first).</summary>
    public static void SetMaxCost(long bytes)
    {
        if (bytes < 0) bytes = 0;
        lock (_lock)
        {
            _maxCost = bytes;
            EvictWhileOverCap();
        }
    }

    /// <summary>Look up by op cache key; null on miss. Hits move to MRU position.</summary>
    public static VipsImage? Get(int key)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Image;
            }
            return null;
        }
    }

    /// <summary>
    /// Insert a freshly-built result. Becomes MRU. Existing entry under the
    /// same key wins — we don't replace, since both produce equivalent
    /// images (same hash) and the existing one may already have downstream
    /// references holding it warm.
    /// </summary>
    public static void Add(int key, VipsImage image)
    {
        long cost = EstimateCost(image);
        lock (_lock)
        {
            if (_map.ContainsKey(key)) return;
            if (_maxCost == 0) return; // caching disabled

            var entry = new Entry { Key = key, Image = image, Cost = cost };
            var node = _lru.AddFirst(entry);
            _map[key] = node;
            _totalCost += cost;
            EvictWhileOverCap();
        }
    }

    /// <summary>Drop everything. Used by tests; production callers rarely need it.</summary>
    public static void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _lru.Clear();
            _totalCost = 0;
        }
    }

    /// <summary>Snapshot of cache size and total cost — for tests / introspection.</summary>
    public static (int Count, long TotalCost) Stats()
    {
        lock (_lock) return (_map.Count, _totalCost);
    }

    private static void EvictWhileOverCap()
    {
        // Caller holds _lock.
        while (_totalCost > _maxCost && _lru.Last != null)
        {
            var victim = _lru.Last.Value;
            _lru.RemoveLast();
            _map.Remove(victim.Key);
            _totalCost -= victim.Cost;
        }
    }

    private static long EstimateCost(VipsImage img)
    {
        // Rough estimate of the materialised buffer size. Lazy images
        // haven't paid this yet, but the cost cap is about cumulative
        // worst-case so the lazy commitment counts.
        return (long)img.Width * img.Height * img.SizeOfPel;
    }
}
