using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace CosmoImage.Core;

/// <summary>
/// Runtime profiler for op-tree execution — port of libvips'
/// <c>gate.c</c>. Off by default (zero overhead in
/// <see cref="VipsImageOps.Run"/>); when enabled, every op invocation
/// is timed and its <c>(CallCount, TotalElapsed)</c> accumulates per
/// <see cref="VipsOperation"/> type name.
///
/// <para>Useful for finding slow stages in a pipeline. Typical use:</para>
/// <code>
///   VipsProfiler.Reset();
///   VipsProfiler.Enabled = true;
///   var result = pipeline.Run();
///   foreach (var (name, stats) in VipsProfiler.Snapshot()
///                                     .OrderByDescending(kv => kv.Value.TotalMilliseconds))
///       Console.WriteLine($"{name}: {stats.CallCount} calls, {stats.TotalMilliseconds:F2} ms");
///   VipsProfiler.Enabled = false;
/// </code>
///
/// <para>Cheap: the only cost when disabled is a single bool read in
/// <c>Run</c>; with profiling on, each op pays one
/// <see cref="Stopwatch.GetTimestamp"/> pair plus a dictionary
/// AddOrUpdate. Op timing measures the full <c>Build</c> +
/// cache-add path, which is appropriate for finding which op types
/// dominate pipeline cost.</para>
/// </summary>
public static class VipsProfiler
{
    private static readonly ConcurrentDictionary<string, Counters> _stats = new();
    private static volatile bool _enabled;

    /// <summary>
    /// Per-op-type accumulated stats. <see cref="TotalMilliseconds"/>
    /// is derived from <see cref="TotalElapsedTicks"/> via
    /// <see cref="Stopwatch.Frequency"/> for portable timing.
    /// </summary>
    public readonly record struct OpStats(int CallCount, long TotalElapsedTicks)
    {
        public double TotalMilliseconds => (double)TotalElapsedTicks * 1000 / Stopwatch.Frequency;
        public double AverageMilliseconds => CallCount == 0 ? 0 : TotalMilliseconds / CallCount;
    }

    /// <summary>
    /// Toggle profiler on/off. Reads from this property on every <c>Run</c>
    /// call, so flip it just before / after the work you want measured.
    /// </summary>
    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>
    /// Open a per-op timing scope. Returns <c>null</c> when profiling is
    /// off so the caller pays no cost. Otherwise the returned disposable
    /// closes the timing window and folds the elapsed ticks into the
    /// stats for <paramref name="opName"/>.
    /// </summary>
    public static IDisposable? Enter(string opName)
    {
        if (!_enabled) return null;
        return new Scope(opName);
    }

    /// <summary>
    /// Read-only snapshot of accumulated stats. Returns a fresh dictionary
    /// per call — safe to enumerate while ops continue to run.
    /// </summary>
    public static IReadOnlyDictionary<string, OpStats> Snapshot()
    {
        var result = new Dictionary<string, OpStats>(_stats.Count);
        foreach (var kv in _stats)
        {
            var c = kv.Value;
            result[kv.Key] = new OpStats(c.CallCount, c.TotalTicks);
        }
        return result;
    }

    /// <summary>Clear all accumulated stats. Doesn't change the enabled flag.</summary>
    public static void Reset() => _stats.Clear();

    /// <summary>Internal mutable counter — folded into the immutable <see cref="OpStats"/> on snapshot.</summary>
    private sealed class Counters
    {
        public int CallCount;
        public long TotalTicks;
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _name;
        private readonly long _start;
        public Scope(string name) { _name = name; _start = Stopwatch.GetTimestamp(); }
        public void Dispose()
        {
            long elapsed = Stopwatch.GetTimestamp() - _start;
            // AddOrUpdate isn't atomic across the two fields in a Counters
            // object, but the two fields aren't observed together (call
            // count and ticks are independent stats), so a simple lock-
            // free increment via Interlocked is enough.
            var c = _stats.GetOrAdd(_name, _ => new Counters());
            System.Threading.Interlocked.Increment(ref c.CallCount);
            System.Threading.Interlocked.Add(ref c.TotalTicks, elapsed);
        }
    }
}
