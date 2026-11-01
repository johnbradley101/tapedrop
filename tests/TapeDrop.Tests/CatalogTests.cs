using TapeDrop.Catalog;

namespace TapeDrop.Tests;

public class CatalogTests
{
    private static JobRecord Job(string tape, params string[] paths) => new(
        "copy", "job", null, tape, "2026-11-01_job",
        DateTime.UtcNow, DateTime.UtcNow, true, 100,
        paths.Select(p => new CatalogFile(p, 10, "ab")).ToList());

    [Fact]
    public void Backup_job_create_list_update_delete()
    {
        using var db = new CatalogDb(":memory:");

        var id = db.CreateBackupJob("Projects", @"E:\Projects", verify: true, ejectWhenDone: false);
        var job = Assert.Single(db.ListBackupJobs());
        Assert.Equal("Projects", job.Name);
        Assert.Equal(@"E:\Projects", job.SourcePath);
        Assert.Null(job.LastRunUtc);

        var when = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
        db.MarkBackupRun(id, when, "TAPE01");
        var updated = db.GetBackupJob(id)!;
        Assert.Equal("TAPE01", updated.LastTapeLabel);
        Assert.Equal(when, updated.LastRunUtc);

        db.DeleteBackupJob(id);
        Assert.Empty(db.ListBackupJobs());
        Assert.Null(db.GetBackupJob(id));
    }

    [Fact]
    public void Duplicate_backup_job_name_is_rejected()
    {
        using var db = new CatalogDb(":memory:");
        db.CreateBackupJob("Same", @"E:\a");
        Assert.Throws<InvalidOperationException>(() => db.CreateBackupJob("Same", @"E:\b"));
    }

    [Fact]
    public void Search_finds_files_and_names_the_tape()
    {
        using var db = new CatalogDb(":memory:");
        db.RecordJob(Job("TAPE01", "2026-11-01_job/photos/IMG_0001.jpg", "2026-11-01_job/notes.txt"));
        db.RecordJob(Job("TAPE02", "2026-11-02_job/photos/IMG_0002.jpg"));

        var hits = db.SearchFiles("img_000");

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.TapeLabel == "TAPE01");
        Assert.Contains(hits, h => h.TapeLabel == "TAPE02");
    }

    [Fact]
    public void Search_treats_percent_and_underscore_literally()
    {
        using var db = new CatalogDb(":memory:");
        db.RecordJob(Job("T", "a_b.txt", "axb.txt", "100%.txt"));

        Assert.Equal("a_b.txt", Assert.Single(db.SearchFiles("a_b")).Path);
        Assert.Equal("100%.txt", Assert.Single(db.SearchFiles("100%")).Path);
    }
}
