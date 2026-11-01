using System.Diagnostics;
using System.Globalization;
using TapeDrop.Core;

namespace TapeDrop.Probe;

/// <summary>
/// Throughput tests. Both use the same planner, hashing and copy engine as the real app, so
/// the numbers show what a real job would achieve.
/// </summary>
public static class SpeedTests
{
    /// <summary>LTO-6 native streaming speed. Below this the drive keeps stopping and repositioning.</summary>
    private const double TapeNativeMBps = 160;

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    public static async Task<int> ReadTestAsync(ProbeArgs args, ProbeReport r, CancellationToken ct = default)
    {
        var source = args.Get("source");
        if (source is null)
        {
            r.Line("Usage: tapedrop-probe read-test --source <folder on the USB RAID> [--gb 2]");
            return 2;
        }
        if (!Directory.Exists(source))
        {
            r.Line($"Source folder not found: {source}");
            return 2;
        }
        if (!TryReadGigabytes(args, r, out var limit)) return 2;

        r.Section("Read speed test (USB RAID to hash)");
        r.Line("Reads files from the RAID and hashes them, exactly as a real job does. Nothing is written.");

        var plan = ProbePlanner.Limit(await Task.Run(() => JobPlanner.Plan(new[] { source }), ct), limit);
        if (!DescribePlan(plan, limit, r)) return 2;

        var sampler = new RateSampler(Window, r.Line);
        var clock = Stopwatch.StartNew();
        long done = 0;
        foreach (var file in plan.Files)
        {
            await Hashing.HashFileAsync(file.SourcePath, n => { done += n; sampler.Mark(done); }, ct);
        }
        clock.Stop();

        Summarise(r, "Read + hash", done, clock.Elapsed, sampler, plan);
        return 0;
    }

    public static async Task<int> CopyTestAsync(ProbeArgs args, ProbeReport r, CancellationToken ct = default)
    {
        var source = args.Get("source");
        var target = args.Get("target");
        if (source is null || target is null)
        {
            r.Line("Usage: tapedrop-probe copy-test --source <folder on the USB RAID> --target <mounted tape, e.g. L:\\> [--gb 2] [--verify] [--keep] [--yes]");
            return 2;
        }
        if (!Directory.Exists(source)) { r.Line($"Source folder not found: {source}"); return 2; }
        if (!Directory.Exists(target)) { r.Line($"Target not found: {target}. Is the tape mounted?"); return 2; }
        if (!TryReadGigabytes(args, r, out var limit)) return 2;

        r.Section("Copy speed test (USB RAID to target)");

        var plan = ProbePlanner.Limit(await Task.Run(() => JobPlanner.Plan(new[] { source }), ct), limit);
        if (!DescribePlan(plan, limit, r)) return 2;

        try
        {
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(target))!).AvailableFreeSpace;
            if (free < plan.TotalBytes + 256L * 1024 * 1024)
            {
                r.Line($"Not enough free space on the target: {free / 1_000_000_000.0:0.0} GB free, " +
                       $"{plan.TotalBytes / 1_000_000_000.0:0.0} GB needed.");
                return 2;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            r.Line("Could not check free space on the target: " + ex.Message);
        }

        var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (!string.IsNullOrEmpty(systemRoot) &&
            string.Equals(Path.GetPathRoot(Path.GetFullPath(target)), systemRoot, StringComparison.OrdinalIgnoreCase))
            r.Line("Note: the target is on your system drive, so this measures that disk, not a tape.");

        var message =
            $"This will write about {plan.TotalBytes / 1_000_000_000.0:0.0} GB to {target}.\n" +
            "On an LTFS tape, space used by test files is NOT given back when they are deleted.\n" +
            "Use a spare tape, never one that holds data you need.";
        if (!Confirm(args, message)) { r.Line("Cancelled. Nothing was written."); return 1; }

        var dest = Path.Combine(target, $"_tapedrop_speedtest_{DateTime.Now:yyyyMMdd_HHmmss}");
        var sampler = new RateSampler(Window, r.Line);
        var clock = Stopwatch.StartNew();
        IReadOnlyList<CopiedFile> copied;
        try
        {
            copied = await CopyEngine.CopyAsync(plan, dest, sampler, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            r.Line("Copy failed: " + ex.Message);
            return 1;
        }
        clock.Stop();

        long bytes = copied.Sum(f => f.Size);
        Summarise(r, "Write", bytes, clock.Elapsed, sampler, plan);

        if (args.Has("verify"))
        {
            r.Line();
            r.Line("Reading the data back and checking it (this may be served from cache, so the speed can look too good)...");
            var readSampler = new RateSampler(Window, r.Line);
            var readClock = Stopwatch.StartNew();
            var result = await Verifier.VerifyAsync(copied, dest, readSampler, ct);
            readClock.Stop();
            r.Line(result.Success
                ? $"Verification passed for {result.FilesChecked:N0} files."
                : $"VERIFICATION FAILED for {result.Failures.Count} file(s). First: {result.Failures[0].RelativePath}: {result.Failures[0].Reason}");
            r.Line($"Read-back speed: {bytes / readClock.Elapsed.TotalSeconds / 1_000_000.0:0.0} MB/s");
        }

        if (args.Has("keep"))
        {
            r.Line($"Test files kept in {dest}");
        }
        else
        {
            try
            {
                Directory.Delete(dest, recursive: true);
                r.Line("Test files deleted.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                r.Line($"Could not delete the test files in {dest}: {ex.Message}");
            }
        }

        return 0;
    }

    private static void Summarise(
        ProbeReport r, string label, long bytes, TimeSpan elapsed, RateSampler sampler, CopyPlan plan)
    {
        var seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        var average = bytes / seconds / 1_000_000.0;

        r.Line();
        r.Line($"{label}: {bytes / 1_000_000_000.0:0.00} GB in {seconds:0.0} s, average {average:0.0} MB/s");

        var summary = RateSampler.Summarize(sampler.WindowRates);
        if (summary is not null)
        {
            r.Line($"Per {Window.TotalSeconds:0}-second window: slowest {summary.Min / 1_000_000.0:0.0}, " +
                   $"typical {summary.Median / 1_000_000.0:0.0}, fastest {summary.Max / 1_000_000.0:0.0} MB/s");
        }
        else
        {
            r.Line("The test was too short to measure windows. Use a larger --gb for a meaningful result.");
        }

        var typical = summary is null ? average : summary.Median / 1_000_000.0;
        var slowest = summary is null ? average : summary.Min / 1_000_000.0;
        r.Line();
        r.Line(typical >= TapeNativeMBps * 1.25
            ? $"Good: typical speed is well above the LTO-6 native rate of {TapeNativeMBps:0} MB/s, so the drive should keep streaming."
            : typical >= TapeNativeMBps
                ? $"Borderline: typical speed is only just above the LTO-6 native rate of {TapeNativeMBps:0} MB/s. Dips could make the drive stop and reposition."
                : $"Too slow: typical speed is below the LTO-6 native rate of {TapeNativeMBps:0} MB/s. The drive will keep stopping and repositioning, which is slow and wears the tape. Check the USB port and cable, file sizes, and antivirus scanning.");
        if (summary is not null && slowest < TapeNativeMBps && typical >= TapeNativeMBps)
            r.Line($"The slowest window ({slowest:0.0} MB/s) dipped below the tape rate; that is normal for brief moments but not for long.");

        if (plan.Files.Count > 0 && plan.TotalBytes / plan.Files.Count < 1_000_000)
            r.Line("Most of these files are under 1 MB. Many small files are much slower than large ones, especially on LTFS.");
    }

    private static bool DescribePlan(CopyPlan plan, long limit, ProbeReport r)
    {
        if (plan.Files.Count == 0)
        {
            r.Line("No files were found in the source folder.");
            return false;
        }

        r.Line($"Using {plan.Files.Count:N0} files, {plan.TotalBytes / 1_000_000_000.0:0.00} GB.");
        if (plan.TotalBytes < limit / 2)
            r.Line($"The source has less data than the {limit / 1_000_000_000.0:0.#} GB asked for, so the test will be short. Point --source at a bigger folder.");
        return true;
    }

    private static bool TryReadGigabytes(ProbeArgs args, ProbeReport r, out long bytes)
    {
        bytes = 0;
        var text = args.Get("gb") ?? "2";
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var gb) || gb <= 0 || gb > 500)
        {
            r.Line("--gb must be a number between 0 and 500.");
            return false;
        }
        bytes = (long)(gb * 1_000_000_000);
        return true;
    }

    private static bool Confirm(ProbeArgs args, string message)
    {
        if (args.Has("yes")) return true;

        Console.WriteLine();
        Console.WriteLine(message);
        if (Console.IsInputRedirected)
        {
            Console.WriteLine("Run again with --yes to confirm.");
            return false;
        }

        Console.Write("Type YES to continue: ");
        return string.Equals(Console.ReadLine()?.Trim(), "YES", StringComparison.Ordinal);
    }
}
