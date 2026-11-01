using TapeDrop.Catalog;
using TapeDrop.Ltfs;

namespace TapeDrop.Core;

public sealed record CopyJobRequest(
    string Name,
    CopyPlan Plan,
    bool Verify = true,
    bool EjectWhenDone = false,
    string Kind = "copy",
    long? BackupJobId = null);

public sealed record CopyJobResult(
    string TapeLabel,
    string DestFolder,
    IReadOnlyList<CopiedFile> Files,
    long Bytes,
    VerifyResult? Verification,
    long? CatalogJobId)
{
    /// <summary>True only if verification ran and every file matched.</summary>
    public bool Verified => Verification?.Success == true;
}

public sealed class InsufficientTapeSpaceException : Exception
{
    public long RequiredBytes { get; }
    public long FreeBytes { get; }

    public InsufficientTapeSpaceException(long requiredBytes, long freeBytes)
        : base($"Not enough space on the tape: {requiredBytes:N0} bytes needed, {freeBytes:N0} free.")
    {
        RequiredBytes = requiredBytes;
        FreeBytes = freeBytes;
    }
}

/// <summary>
/// Runs one copy job end to end: mount, check space, copy and hash, write manifests,
/// unmount (writes the LTFS index), optionally remount and verify, record in the catalog,
/// optionally eject. Used by both the drag-and-drop copy job and saved backup jobs.
/// </summary>
public sealed class CopyJobRunner
{
    /// <summary>Headroom left for the LTFS index and metadata.</summary>
    public const long SafetyMarginBytes = 64L * 1024 * 1024;

    private readonly ILtfsProvider _tape;
    private readonly CatalogDb? _catalog;

    public CopyJobRunner(ILtfsProvider tape, CatalogDb? catalog = null)
    {
        _tape = tape;
        _catalog = catalog;
    }

    public async Task<CopyJobResult> RunAsync(
        CopyJobRequest request,
        IProgress<CopyProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (request.Plan.Files.Count == 0)
            throw new InvalidOperationException("There is nothing to copy.");

        var startedUtc = DateTime.UtcNow;
        void Phase(string phase) =>
            progress?.Report(new CopyProgress(phase, "", 0, request.Plan.TotalBytes, 0, request.Plan.Files.Count, 0));

        var mounted = false;
        try
        {
            Phase("Mounting");
            var info = await _tape.MountAsync(ct);
            mounted = true;

            if (request.Plan.TotalBytes + SafetyMarginBytes > info.FreeBytes)
                throw new InsufficientTapeSpaceException(request.Plan.TotalBytes + SafetyMarginBytes, info.FreeBytes);

            var tapeLabel = TapeIdentity.Resolve(info.MountPath, info.Label);
            var destFolder = PickFolderName(info.MountPath, request.Name);
            var destRoot = Path.Combine(info.MountPath, destFolder);
            Directory.CreateDirectory(destRoot);

            var copied = await CopyEngine.CopyAsync(request.Plan, destRoot, progress, ct);

            ManifestWriter.Write(info.MountPath, destFolder, new TapeJobManifest(
                request.Name, request.Kind, startedUtc, tapeLabel, destFolder, "TapeDrop", copied));

            Phase("Writing index");
            await _tape.UnmountAsync(ct);
            mounted = false;

            VerifyResult? verification = null;
            if (request.Verify)
            {
                Phase("Verifying");
                var remounted = await _tape.MountAsync(ct);
                mounted = true;
                verification = await Verifier.VerifyAsync(
                    copied, Path.Combine(remounted.MountPath, destFolder), progress, ct);
                await _tape.UnmountAsync(ct);
                mounted = false;
            }

            long bytes = 0;
            foreach (var f in copied) bytes += f.Size;

            long? catalogJobId = null;
            if (_catalog is not null)
            {
                catalogJobId = _catalog.RecordJob(new JobRecord(
                    request.Kind,
                    request.Name,
                    request.BackupJobId,
                    tapeLabel,
                    destFolder,
                    startedUtc,
                    DateTime.UtcNow,
                    verification?.Success == true,
                    bytes,
                    copied.Select(f => new CatalogFile(destFolder + "/" + f.RelativePath, f.Size, f.Sha256)).ToList()));
            }

            // Never eject a tape that failed verification: the user should look at it first.
            if (request.EjectWhenDone && (verification?.Success ?? true))
                await _tape.EjectAsync(ct);

            Phase("Done");
            return new CopyJobResult(tapeLabel, destFolder, copied, bytes, verification, catalogJobId);
        }
        finally
        {
            // Always try to unmount so LTFS writes its index, even after a failure or cancel.
            if (mounted)
            {
                try { await _tape.UnmountAsync(CancellationToken.None); }
                catch { /* best effort */ }
            }
        }
    }

    private static string PickFolderName(string tapeRoot, string jobName)
    {
        var baseName = $"{DateTime.Now:yyyy-MM-dd}_{NameSanitizer.Sanitize(jobName)}";
        var candidate = baseName;
        for (var n = 2; Directory.Exists(Path.Combine(tapeRoot, candidate)); n++)
            candidate = $"{baseName}_{n}";
        return candidate;
    }
}
