using System;
using CosmoImage.Core;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 177 — LRU + cost-based <see cref="VipsCache"/>. Replaces the
/// count-based clear-everything eviction. Tests pin: hit-then-miss after
/// eviction, recency refresh on Get, immediate trim on cap shrink, and
/// disable-when-cap-is-zero behaviour.
///
/// <para>Each test uses a fresh <see cref="VipsCache.Clear"/> + cap
/// override so it doesn't interfere with the rest of the suite (which
/// runs production ops through the same shared cache). The tests
/// restore the default cap on completion.</para>
/// </summary>
public class Round177Tests : IDisposable
{
    private readonly long _originalCap;
    public Round177Tests()
    {
        _originalCap = VipsCache.MaxCost;
        VipsCache.Clear();
    }

    public void Dispose()
    {
        VipsCache.Clear();
        VipsCache.SetMaxCost(_originalCap);
    }

    /// <summary>Synthesise a tiny image of known cost = w·h·1 (UChar 1-band).</summary>
    private static VipsImage Tiny(int w, int h)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => 0,
        };

    [Fact]
    public void GetMiss_ReturnsNull()
    {
        Assert.Null(VipsCache.Get(42));
    }

    [Fact]
    public void AddThenGet_ReturnsSameImage()
    {
        var img = Tiny(10, 10);
        VipsCache.SetMaxCost(1024);
        VipsCache.Add(1, img);
        Assert.Same(img, VipsCache.Get(1));
    }

    [Fact]
    public void Stats_ReflectsAddAndCost()
    {
        VipsCache.SetMaxCost(10000);
        VipsCache.Add(1, Tiny(10, 10));   // cost 100
        VipsCache.Add(2, Tiny(20, 20));   // cost 400
        var (count, cost) = VipsCache.Stats();
        Assert.Equal(2, count);
        Assert.Equal(500, cost);
    }

    [Fact]
    public void OverCap_EvictsOldestFirst()
    {
        // Cap = 250. Add three 100-cost images: oldest one drops when
        // total exceeds 250.
        VipsCache.SetMaxCost(250);
        var a = Tiny(10, 10);
        var b = Tiny(10, 10);
        var c = Tiny(10, 10);
        VipsCache.Add(1, a);
        VipsCache.Add(2, b);
        VipsCache.Add(3, c);  // adds 100 → total 300 > 250, evict key 1.
        Assert.Null(VipsCache.Get(1));
        Assert.Same(b, VipsCache.Get(2));
        Assert.Same(c, VipsCache.Get(3));
    }

    [Fact]
    public void Get_RefreshesRecency_KeepsItemAlive()
    {
        VipsCache.SetMaxCost(250);
        var a = Tiny(10, 10);
        var b = Tiny(10, 10);
        var c = Tiny(10, 10);
        VipsCache.Add(1, a);
        VipsCache.Add(2, b);
        // Touch key 1 → moves to MRU. Now key 2 is the LRU.
        Assert.Same(a, VipsCache.Get(1));
        VipsCache.Add(3, c);  // Evicts key 2, not key 1.
        Assert.Same(a, VipsCache.Get(1));
        Assert.Null(VipsCache.Get(2));
        Assert.Same(c, VipsCache.Get(3));
    }

    [Fact]
    public void SetMaxCost_LowerThanCurrent_TrimsImmediately()
    {
        VipsCache.SetMaxCost(10000);
        VipsCache.Add(1, Tiny(50, 50));   // cost 2500
        VipsCache.Add(2, Tiny(50, 50));   // cost 2500, total 5000
        VipsCache.SetMaxCost(2500);       // tail-trim: evict key 1.
        Assert.Null(VipsCache.Get(1));
        Assert.NotNull(VipsCache.Get(2));
    }

    [Fact]
    public void DisableViaZeroCap_AddIsNoOp()
    {
        VipsCache.SetMaxCost(0);
        VipsCache.Add(1, Tiny(10, 10));
        Assert.Null(VipsCache.Get(1));
        var (count, cost) = VipsCache.Stats();
        Assert.Equal(0, count);
        Assert.Equal(0, cost);
    }

    [Fact]
    public void DuplicateKey_FirstAddWins()
    {
        VipsCache.SetMaxCost(10000);
        var a = Tiny(10, 10);
        var b = Tiny(20, 20);
        VipsCache.Add(1, a);
        VipsCache.Add(1, b);  // ignored — same key
        Assert.Same(a, VipsCache.Get(1));
        var (count, cost) = VipsCache.Stats();
        Assert.Equal(1, count);
        Assert.Equal(100, cost);  // a's cost only
    }

    [Fact]
    public void CostScalesWithPelSize()
    {
        // Float 4-band 10×10 → 10·10·16 = 1600.
        var floatRgba = new VipsImage
        {
            Width = 10, Height = 10, Bands = 4, BandFormat = VipsBandFormat.Float,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => 0,
        };
        VipsCache.SetMaxCost(10000);
        VipsCache.Add(1, floatRgba);
        var (_, cost) = VipsCache.Stats();
        Assert.Equal(1600, cost);
    }
}
