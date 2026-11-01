using System.Diagnostics;

namespace TapeDrop.Core;

public sealed record VerifyFailure(string RelativePath, string Reason);

public sealed record VerifyResult(int FilesChecked, IReadOnlyList<VerifyFailure> Failures)
{
    public bool Success => Failures.Count == 0;
}

/// <summary>
/// Re-reads files from the destination and compares their SHA-256 against the hashes taken
/// from the source during the copy. The caller should remount the tape first so the data
/// is read back from tape rather than from a cache.
/// </summary>
public static class Verifier
{
    public static async Task<VerifyResult> VerifyAsync(
        IReadOnlyList<CopiedFile> files,
        string root,
        IProgress<CopyProgress>? progress = null,
        CancellationToken ct = default)
    {
        var failures = new List<VerifyFailure>();
        long totalBytes = 0;
        foreach (var f in files) totalBytes += f.Size;

        var clock = Stopwatch.StartNew();
        long doneBytes = 0;
        long lastReportMs = -1000;
        int checkedCount = 0;

        void Report(string current, bool force)
        {
            if (progress == null) return;
            var ms = clock.ElapsedMilliseconds;
            if (!force && ms - lastReportMs < 200) return;
            lastReportMs = ms;
            var rate = ms > 0 ? doneBytes / (ms / 1000.0) : 0;
            progress.Report(new CopyProgress(
                "Verifying", current, doneBytes, totalBytes, checkedCount, files.Count, rate));
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Report(file.RelativePath, true);

            if (!File.Exists(path))
            {
                failures.Add(new VerifyFailure(file.RelativePath, "Missing on tape"));
            }
            else
            {
                try
                {
                    var hash = await Hashing.HashFileAsync(path, n => { doneBytes += n; Report(file.RelativePath, false); }, ct);
                    if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                        failures.Add(new VerifyFailure(file.RelativePath, "Checksum mismatch"));
                }
                catch (IOException ex)
                {
                    failures.Add(new VerifyFailure(file.RelativePath, "Read error: " + ex.Message));
                }
            }

            checkedCount++;
        }

        Report("", true);
        return new VerifyResult(checkedCount, failures);
    }
}
