using TapeDrop.Catalog;
using TapeDrop.Ltfs;

namespace TapeDrop.Core;

/// <summary>Thrown when a backup job's source folder is missing or empty.</summary>
public sealed class SourceUnavailableException : Exception
{
    public SourceUnavailableException(string message) : base(message) { }
}

/// <summary>
/// Runs a saved backup job: writes every file in the saved source folder to the tape
/// that is currently loaded, using the same engine as the simple copy job.
/// </summary>
public sealed class BackupJobRunner
{
    private readonly ILtfsProvider _tape;
    private readonly CatalogDb _catalog;

    public BackupJobRunner(ILtfsProvider tape, CatalogDb catalog)
    {
        _tape = tape;
        _catalog = catalog;
    }

    public async Task<CopyJobResult> RunAsync(
        BackupJobDef job,
        IProgress<CopyProgress>? progress = null,
        CancellationToken ct = default)
    {
        // Refuse to run rather than write an empty backup if the RAID is unplugged.
        if (!Directory.Exists(job.SourcePath))
            throw new SourceUnavailableException(
                $"The source folder was not found: {job.SourcePath}. Is the USB RAID connected?");

        var plan = JobPlanner.Plan(new[] { job.SourcePath });
        if (plan.Files.Count == 0)
            throw new SourceUnavailableException($"The source folder has no files to back up: {job.SourcePath}");

        var request = new CopyJobRequest(job.Name, plan, job.Verify, job.EjectWhenDone, "backup", job.Id);
        var result = await new CopyJobRunner(_tape, _catalog).RunAsync(request, progress, ct);

        _catalog.MarkBackupRun(job.Id, DateTime.UtcNow, result.TapeLabel);
        return result;
    }
}
