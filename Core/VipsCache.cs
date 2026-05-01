using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace CosmoImage.Core;

internal static class VipsCache
{
    private static readonly ConcurrentDictionary<int, VipsImage> _cache = new();
    private const int MaxCacheSize = 1000;

    public static VipsImage? Get(int key)
    {
        if (_cache.TryGetValue(key, out var image))
        {
            return image;
        }
        return null;
    }

    public static void Add(int key, VipsImage image)
    {
        if (_cache.Count >= MaxCacheSize)
        {
            // Simple eviction: clear everything if full (real vips is smarter)
            _cache.Clear();
        }
        _cache.TryAdd(key, image);
    }
}
