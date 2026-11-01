using TapeDrop.Core;

namespace TapeDrop.Tests;

public class CopyEngineTests
{
    [Fact]
    public async Task Copy_preserves_content_hash_and_modified_time()
    {
        using var t = new TempDir();
        // Larger than one 4 MB block so the multi-block path is exercised.
        var data = TestData.Random(9 * 1024 * 1024 + 123);
        var src = t.Make("src/big.bin", data);
        var stamp = new DateTime(2024, 5, 17, 10, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(src, stamp);

        var plan = JobPlanner.Plan(new[] { src });
        var dest = t.Sub("dest");
        var copied = await CopyEngine.CopyAsync(plan, dest);

        var destFile = System.IO.Path.Combine(dest, "big.bin");
        Assert.Equal(data, File.ReadAllBytes(destFile));
        Assert.Equal(TestData.Sha256(data), copied.Single().Sha256);
        Assert.Equal(data.Length, copied.Single().Size);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(destFile));
    }

    [Fact]
    public async Task Empty_files_are_copied()
    {
        using var t = new TempDir();
        var src = t.Make("src/empty.txt", Array.Empty<byte>());

        var copied = await CopyEngine.CopyAsync(JobPlanner.Plan(new[] { src }), t.Sub("dest"));

        Assert.Equal(TestData.Sha256(Array.Empty<byte>()), copied.Single().Sha256);
        Assert.True(File.Exists(System.IO.Path.Combine(t.Root, "dest", "empty.txt")));
    }

    [Fact]
    public async Task Progress_ends_at_the_total_byte_count()
    {
        using var t = new TempDir();
        var a = t.Make("src/a.bin", TestData.Random(1000, 1));
        var b = t.Make("src/b.bin", TestData.Random(2000, 2));
        var progress = new SyncProgress();

        await CopyEngine.CopyAsync(JobPlanner.Plan(new[] { a, b }), t.Sub("dest"), progress);

        var last = progress.Items.Last();
        Assert.Equal(3000, last.BytesDone);
        Assert.Equal(3000, last.TotalBytes);
        Assert.Equal(2, last.FilesDone);
    }

    [Fact]
    public async Task Cancellation_stops_the_copy()
    {
        using var t = new TempDir();
        var src = t.Make("src/a.bin", TestData.Random(5000));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CopyEngine.CopyAsync(JobPlanner.Plan(new[] { src }), t.Sub("dest"), null, cts.Token));
    }
}

public class VerifierTests
{
    [Fact]
    public async Task Verify_passes_for_an_intact_copy()
    {
        using var t = new TempDir();
        var src = t.Make("src/a.bin", TestData.Random(5000));
        var dest = t.Sub("dest");
        var copied = await CopyEngine.CopyAsync(JobPlanner.Plan(new[] { src }), dest);

        var result = await Verifier.VerifyAsync(copied, dest);

        Assert.True(result.Success);
        Assert.Equal(1, result.FilesChecked);
    }

    [Fact]
    public async Task Verify_detects_a_corrupted_file()
    {
        using var t = new TempDir();
        var src = t.Make("src/a.bin", TestData.Random(5000));
        var dest = t.Sub("dest");
        var copied = await CopyEngine.CopyAsync(JobPlanner.Plan(new[] { src }), dest);

        var destFile = System.IO.Path.Combine(dest, "a.bin");
        var bytes = File.ReadAllBytes(destFile);
        bytes[100] ^= 0xFF;
        File.WriteAllBytes(destFile, bytes);

        var result = await Verifier.VerifyAsync(copied, dest);

        Assert.False(result.Success);
        Assert.Equal("Checksum mismatch", result.Failures.Single().Reason);
    }

    [Fact]
    public async Task Verify_detects_a_missing_file()
    {
        using var t = new TempDir();
        var src = t.Make("src/a.bin", TestData.Random(5000));
        var dest = t.Sub("dest");
        var copied = await CopyEngine.CopyAsync(JobPlanner.Plan(new[] { src }), dest);
        File.Delete(System.IO.Path.Combine(dest, "a.bin"));

        var result = await Verifier.VerifyAsync(copied, dest);

        Assert.False(result.Success);
        Assert.Equal("Missing on tape", result.Failures.Single().Reason);
    }
}
