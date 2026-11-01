using TapeDrop.Catalog;
using TapeDrop.Core;
using TapeDrop.Ltfs;

namespace TapeDrop.Tests;

public class CopyJobRunnerTests
{
    private const long OneGb = 1024L * 1024 * 1024;

    [Fact]
    public async Task Copy_job_writes_verifies_and_catalogs_the_files()
    {
        using var t = new TempDir();
        var data = TestData.Random(300_000);
        var src = t.Sub("raid");
        t.Make("raid/photos/a.bin", data);
        t.Make("raid/photos/b.bin", TestData.Random(1234, 9));

        var tape = new FolderLtfsProvider(t.Sub("tape"), OneGb, "TAPE01");
        using var catalog = new CatalogDb(":memory:");
        var plan = JobPlanner.Plan(new[] { System.IO.Path.Combine(src, "photos") });

        var result = await new CopyJobRunner(tape, catalog).RunAsync(new CopyJobRequest("holiday", plan));

        Assert.True(result.Verified);
        Assert.Equal("TAPE01", result.TapeLabel);
        Assert.Equal(2, tape.MountCount);          // once to write, once to verify from tape
        Assert.False(tape.IsMounted);               // always left unmounted so the index is written
        var onTape = System.IO.Path.Combine(t.Root, "tape", result.DestFolder, "photos", "a.bin");
        Assert.Equal(data, File.ReadAllBytes(onTape));

        var hit = Assert.Single(catalog.SearchFiles("a.bin"));
        Assert.Equal("TAPE01", hit.TapeLabel);
        Assert.Equal(result.DestFolder + "/photos/a.bin", hit.Path);
    }

    [Fact]
    public async Task Manifest_on_tape_matches_sha256sum_format_and_the_data()
    {
        using var t = new TempDir();
        t.Make("raid/docs/one.txt", TestData.Random(500, 3));
        t.Make("raid/docs/two.txt", TestData.Random(700, 4));
        var tapeRoot = t.Sub("tape");
        var tape = new FolderLtfsProvider(tapeRoot, OneGb, "TAPE01");
        var plan = JobPlanner.Plan(new[] { System.IO.Path.Combine(t.Root, "raid", "docs") });

        var result = await new CopyJobRunner(tape).RunAsync(new CopyJobRequest("docs", plan, Verify: false));

        var manifest = System.IO.Path.Combine(tapeRoot, "_tapedrop", result.DestFolder + ".sha256");
        var lines = File.ReadAllLines(manifest);
        Assert.Equal(2, lines.Length);
        foreach (var line in lines)
        {
            // "<64 hex chars>  <path relative to tape root>", exactly what `sha256sum -c` expects.
            Assert.Equal("  ", line.Substring(64, 2));
            var hash = line[..64];
            var path = line[66..];
            var actual = TestData.Sha256(File.ReadAllBytes(System.IO.Path.Combine(tapeRoot, path.Replace('/', System.IO.Path.DirectorySeparatorChar))));
            Assert.Equal(actual, hash);
        }
        Assert.True(File.Exists(System.IO.Path.Combine(tapeRoot, "_tapedrop", result.DestFolder + ".json")));
    }

    [Fact]
    public async Task Skipping_verification_mounts_only_once()
    {
        using var t = new TempDir();
        var src = t.Make("raid/a.bin", TestData.Random(100));
        var tape = new FolderLtfsProvider(t.Sub("tape"), OneGb, "T");

        var result = await new CopyJobRunner(tape).RunAsync(
            new CopyJobRequest("x", JobPlanner.Plan(new[] { src }), Verify: false));

        Assert.Equal(1, tape.MountCount);
        Assert.False(result.Verified);
        Assert.Null(result.Verification);
    }

    [Fact]
    public async Task Too_big_for_the_tape_is_rejected_before_writing_and_tape_is_unmounted()
    {
        using var t = new TempDir();
        var src = t.Make("raid/a.bin", TestData.Random(1000));
        var tapeRoot = t.Sub("tape");
        var tape = new FolderLtfsProvider(tapeRoot, 1000, "TINY");

        await Assert.ThrowsAsync<InsufficientTapeSpaceException>(() =>
            new CopyJobRunner(tape).RunAsync(new CopyJobRequest("x", JobPlanner.Plan(new[] { src }))));

        Assert.False(tape.IsMounted);
        Assert.Empty(Directory.GetFileSystemEntries(tapeRoot));
    }

    [Fact]
    public async Task Unlabelled_tape_gets_a_stable_id_across_jobs()
    {
        using var t = new TempDir();
        var src = t.Make("raid/a.bin", TestData.Random(100));
        var tape = new FolderLtfsProvider(t.Sub("tape"), OneGb, "");
        var runner = new CopyJobRunner(tape);
        var request = new CopyJobRequest("x", JobPlanner.Plan(new[] { src }), Verify: false);

        var first = await runner.RunAsync(request);
        var second = await runner.RunAsync(request);

        Assert.StartsWith("TAPE-", first.TapeLabel);
        Assert.Equal(first.TapeLabel, second.TapeLabel);
        Assert.NotEqual(first.DestFolder, second.DestFolder); // same day + name must not overwrite
    }

    [Fact]
    public async Task Eject_after_a_successful_job()
    {
        using var t = new TempDir();
        var src = t.Make("raid/a.bin", TestData.Random(100));
        var tape = new FolderLtfsProvider(t.Sub("tape"), OneGb, "T");

        await new CopyJobRunner(tape).RunAsync(
            new CopyJobRequest("x", JobPlanner.Plan(new[] { src }), Verify: true, EjectWhenDone: true));

        await Assert.ThrowsAsync<TapeNotReadyException>(() => tape.MountAsync());
    }

    [Fact]
    public async Task Empty_selection_is_rejected()
    {
        var tape = new FolderLtfsProvider();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CopyJobRunner(tape).RunAsync(new CopyJobRequest("x", new CopyPlan(Array.Empty<PlannedFile>(), 0, Array.Empty<string>()))));
    }
}

public class BackupJobRunnerTests
{
    private const long OneGb = 1024L * 1024 * 1024;

    [Fact]
    public async Task Missing_source_folder_refuses_to_run()
    {
        using var t = new TempDir();
        using var catalog = new CatalogDb(":memory:");
        var id = catalog.CreateBackupJob("Projects", System.IO.Path.Combine(t.Root, "not-plugged-in"));
        var tape = new FolderLtfsProvider(t.Sub("tape"), OneGb, "T1");

        await Assert.ThrowsAsync<SourceUnavailableException>(() =>
            new BackupJobRunner(tape, catalog).RunAsync(catalog.GetBackupJob(id)!));

        Assert.Equal(0, tape.MountCount); // never touched the tape
    }

    [Fact]
    public async Task Empty_source_folder_refuses_to_run()
    {
        using var t = new TempDir();
        using var catalog = new CatalogDb(":memory:");
        var id = catalog.CreateBackupJob("Empty", t.Sub("empty"));
        var tape = new FolderLtfsProvider(t.Sub("tape"), OneGb, "T1");

        await Assert.ThrowsAsync<SourceUnavailableException>(() =>
            new BackupJobRunner(tape, catalog).RunAsync(catalog.GetBackupJob(id)!));
    }

    [Fact]
    public async Task Backup_writes_the_folder_records_history_and_updates_last_run()
    {
        using var t = new TempDir();
        t.Make("projects/site/index.html", TestData.Random(400, 5));
        t.Make("projects/readme.md", TestData.Random(50, 6));
        using var catalog = new CatalogDb(":memory:");
        var id = catalog.CreateBackupJob("Projects", System.IO.Path.Combine(t.Root, "projects"));
        var tape = new FolderLtfsProvider(t.Sub("tape"), OneGb, "T1");

        var result = await new BackupJobRunner(tape, catalog).RunAsync(catalog.GetBackupJob(id)!);

        Assert.True(result.Verified);
        Assert.True(File.Exists(System.IO.Path.Combine(t.Root, "tape", result.DestFolder, "projects", "site", "index.html")));

        var job = catalog.GetBackupJob(id)!;
        Assert.Equal("T1", job.LastTapeLabel);
        Assert.NotNull(job.LastRunUtc);

        var hit = Assert.Single(catalog.SearchFiles("index.html"));
        Assert.Equal("Projects", hit.JobName);
    }
}
