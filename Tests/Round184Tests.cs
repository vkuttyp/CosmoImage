using System;
using System.Linq;
using CosmoImage.Core;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 184 — <see cref="VipsProfiler"/>: per-op-type runtime profiler
/// wired into <see cref="VipsImageOps.Run"/>. Off by default; toggle on
/// to find slow stages in a pipeline.
///
/// <para>Tests verify:</para>
/// <list type="bullet">
///   <item>Disabled profiler stays empty no matter how many ops run.</item>
///   <item>Enabled profiler accumulates per-op-type call counts.</item>
///   <item>Reset clears state without touching the Enabled flag.</item>
///   <item>Snapshot is a defensive copy — adding ops after snapshotting
///         doesn't mutate the caller's dictionary.</item>
/// </list>
///
/// <para>Each test resets state in setup and disables the profiler in
/// teardown, since the profiler is a process-global so co-mingling
/// state with other tests would be fragile.</para>
/// </summary>
public class Round184Tests : IDisposable
{
    public Round184Tests()
    {
        // Cache eviction would prevent the per-op call counts from
        // matching what we expect since the second invocation hits the
        // cache and skips Run. Disable cache for these tests by setting
        // its cap to 0; restore in Dispose.
        _origMaxCost = VipsCache.MaxCost;
        VipsCache.SetMaxCost(0);
        VipsCache.Clear();
        VipsProfiler.Reset();
        VipsProfiler.Enabled = false;
    }
    private readonly long _origMaxCost;
    public void Dispose()
    {
        VipsProfiler.Enabled = false;
        VipsProfiler.Reset();
        VipsCache.SetMaxCost(_origMaxCost);
    }

    private static VipsImage Tiny()
        => new VipsImage
        {
            Width = 4, Height = 4, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => 0,
        };

    [Fact]
    public void Disabled_NoStatsAccumulated()
    {
        var img = Tiny();
        // Run a few ops without enabling the profiler.
        _ = img.Invert();
        _ = img.Invert();
        var snap = VipsProfiler.Snapshot();
        Assert.Empty(snap);
    }

    [Fact]
    public void Enabled_AccumulatesCallCountPerOpType()
    {
        VipsProfiler.Enabled = true;
        var img = Tiny();
        _ = img.Invert();
        _ = img.Invert();
        _ = img.Linearize();
        var snap = VipsProfiler.Snapshot();
        // Some op types are present; specific names depend on impl.
        Assert.NotEmpty(snap);
        // Total call count across all op types should be ≥ ops we ran
        // (ops may compose internally, so >=).
        Assert.True(snap.Values.Sum(s => s.CallCount) >= 3);
    }

    [Fact]
    public void Stats_TimesAreNonZero()
    {
        VipsProfiler.Enabled = true;
        var img = Tiny();
        _ = img.Invert();
        var snap = VipsProfiler.Snapshot();
        Assert.True(snap.Values.Any(s => s.TotalElapsedTicks > 0));
        Assert.True(snap.Values.Any(s => s.TotalMilliseconds >= 0));
    }

    [Fact]
    public void Reset_ClearsStatsButNotEnabledFlag()
    {
        VipsProfiler.Enabled = true;
        var img = Tiny();
        _ = img.Invert();
        Assert.NotEmpty(VipsProfiler.Snapshot());

        VipsProfiler.Reset();
        Assert.Empty(VipsProfiler.Snapshot());
        Assert.True(VipsProfiler.Enabled);  // Reset doesn't touch Enabled

        // Run again; stats accumulate from zero.
        _ = img.Invert();
        Assert.NotEmpty(VipsProfiler.Snapshot());
    }

    [Fact]
    public void Snapshot_IsDefensiveCopy()
    {
        VipsProfiler.Enabled = true;
        var img = Tiny();
        _ = img.Invert();
        var first = VipsProfiler.Snapshot();
        int firstCount = first.Values.Sum(s => s.CallCount);

        // Run more ops; the original snapshot shouldn't change.
        _ = img.Invert();
        _ = img.Invert();
        Assert.Equal(firstCount, first.Values.Sum(s => s.CallCount));

        // A fresh snapshot reflects the new ops.
        var second = VipsProfiler.Snapshot();
        Assert.True(second.Values.Sum(s => s.CallCount) > firstCount);
    }

    [Fact]
    public void AverageMilliseconds_ScalesWithCallCount()
    {
        VipsProfiler.Enabled = true;
        var img = Tiny();
        for (int i = 0; i < 5; i++) _ = img.Invert();
        var snap = VipsProfiler.Snapshot();
        // Find any op type with multiple calls and verify the average
        // is total / count.
        var multiCalled = snap.Values.FirstOrDefault(s => s.CallCount >= 2);
        if (multiCalled.CallCount >= 2)
        {
            double avg = multiCalled.AverageMilliseconds;
            double tot = multiCalled.TotalMilliseconds;
            Assert.Equal(tot / multiCalled.CallCount, avg, 1e-6);
        }
    }
}
