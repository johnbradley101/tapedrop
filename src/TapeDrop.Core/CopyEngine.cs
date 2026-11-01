using System.Buffers;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace TapeDrop.Core;

public sealed record CopiedFile(string RelativePath, long Size, string Sha256);

/// <param name="Phase">Mounting, Copying, Writing index, Verifying or Done.</param>
public sealed record CopyProgress(
    string Phase,
    string CurrentFile,
    long BytesDone,
    long TotalBytes,
    int FilesDone,
    int FilesTotal,
    double BytesPerSecond);

/// <summary>
/// Streams files to the destination with large blocks. A reader task keeps a few blocks
/// queued ahead of the writer so the source drive keeps the tape streaming instead of
/// stalling it, and the SHA-256 is computed during that single read of the source.
/// </summary>
public static class CopyEngine
{
    public const int BlockSize = 4 * 1024 * 1024;
    private const int QueueDepth = 4;

    public static async Task<IReadOnlyList<CopiedFile>> CopyAsync(
        CopyPlan plan,
        string destRoot,
        IProgress<CopyProgress>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<CopiedFile>(plan.Files.Count);
        var clock = Stopwatch.StartNew();
        long doneBytes = 0;
        long lastReportMs = -1000;
        int filesDone = 0;

        void Report(string current, bool force)
        {
            if (progress == null) return;
            var ms = clock.ElapsedMilliseconds;
            if (!force && ms - lastReportMs < 200) return;
            lastReportMs = ms;
            var rate = ms > 0 ? doneBytes / (ms / 1000.0) : 0;
            progress.Report(new CopyProgress(
                "Copying", current, doneBytes, plan.TotalBytes, filesDone, plan.Files.Count, rate));
        }

        foreach (var file in plan.Files)
        {
            ct.ThrowIfCancellationRequested();

            var dest = Path.Combine(destRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            Report(file.RelativePath, true);
            var (hash, bytes) = await CopyOneAsync(
                file.SourcePath,
                dest,
                n => { doneBytes += n; Report(file.RelativePath, false); },
                ct);

            File.SetLastWriteTimeUtc(dest, file.LastWriteUtc);
            filesDone++;
            results.Add(new CopiedFile(file.RelativePath, bytes, hash));
        }

        Report("", true);
        return results;
    }

    private static async Task<(string Hash, long Bytes)> CopyOneAsync(
        string source,
        string dest,
        Action<int> onWritten,
        CancellationToken ct)
    {
        var channel = Channel.CreateBounded<(byte[] Buffer, int Length)>(
            new BoundedChannelOptions(QueueDepth) { SingleReader = true, SingleWriter = true });

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = cts.Token;
        long total = 0;

        var readerTask = Task.Run(async () =>
        {
            try
            {
                await using var input = new FileStream(
                    source, FileMode.Open, FileAccess.Read, FileShare.Read, 1,
                    FileOptions.SequentialScan | FileOptions.Asynchronous);

                while (true)
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(BlockSize);
                    int n;
                    try
                    {
                        n = await input.ReadAsync(buffer.AsMemory(0, BlockSize), token);
                    }
                    catch
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        throw;
                    }

                    if (n == 0)
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        break;
                    }

                    hasher.AppendData(buffer, 0, n);
                    await channel.Writer.WriteAsync((buffer, n), token);
                }

                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
                throw;
            }
        }, token);

        try
        {
            await using var output = new FileStream(
                dest, FileMode.Create, FileAccess.Write, FileShare.None, 1,
                FileOptions.SequentialScan | FileOptions.Asynchronous);

            await foreach (var (buffer, length) in channel.Reader.ReadAllAsync(token))
            {
                try
                {
                    await output.WriteAsync(buffer.AsMemory(0, length), token);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                total += length;
                onWritten(length);
            }

            await output.FlushAsync(token);
            await readerTask;
        }
        catch
        {
            // Stop the reader and prefer its error over the channel's wrapper exception.
            cts.Cancel();
            Exception? readerError = null;
            try { await readerTask; } catch (Exception e) { readerError = e; }

            if (readerError is not null && readerError is not OperationCanceledException)
                ExceptionDispatchInfo.Capture(readerError).Throw();
            throw;
        }

        return (Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant(), total);
    }
}
