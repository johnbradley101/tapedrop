using System.Diagnostics;
using TapeDrop.Core;

namespace TapeDrop.Probe;

public static class ProbePlanner
{
    /// <summary>
    /// Takes files in plan order until at least <paramref name="limitBytes"/> is reached.
    /// Whole files only, so the last file can push the total a little past the limit.
    /// </summary>
    public static CopyPlan Limit(CopyPlan plan, long limitBytes)
    {
        var files = new List<PlannedFile>();
        long total = 0;
        foreach (var file in plan.Files)
        {
            if (total >= limitBytes) break;
            files.Add(file);
            total += file.Size;
        }
        return new CopyPlan(files, total, plan.Warnings);
    }
}

public sealed record RateSummary(double Min, double Median, double Max);

/// <summary>Records throughput over fixed time windows so dips (not just the average) show up.</summary>
public sealed class RateSampler : IProgress<CopyProgress>
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TimeSpan _window;
    private readonly Action<string>? _onWindow;
    private double _lastMs;
    private long _lastBytes;
    private readonly List<double> _rates = new();

    public RateSampler(TimeSpan window, Action<string>? onWindow = null)
    {
        _window = window;
        _onWindow = onWindow;
    }

    /// <summary>Bytes per second for each completed window.</summary>
    public IReadOnlyList<double> WindowRates => _rates;

    public void Report(CopyProgress value) => Mark(value.BytesDone);

    public void Mark(long bytesDone)
    {
        var ms = _clock.Elapsed.TotalMilliseconds;
        if (ms - _lastMs < _window.TotalMilliseconds) return;

        var rate = (bytesDone - _lastBytes) / ((ms - _lastMs) / 1000.0);
        _rates.Add(rate);
        _lastMs = ms;
        _lastBytes = bytesDone;
        _onWindow?.Invoke($"  {ms / 1000.0,6:0}s  {rate / 1_000_000.0,7:0.0} MB/s   ({bytesDone / 1_000_000_000.0:0.00} GB so far)");
    }

    public static RateSummary? Summarize(IReadOnlyList<double> rates)
    {
        if (rates.Count == 0) return null;
        var sorted = rates.OrderBy(r => r).ToList();
        var middle = sorted.Count / 2;
        var median = sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        return new RateSummary(sorted[0], median, sorted[^1]);
    }
}
