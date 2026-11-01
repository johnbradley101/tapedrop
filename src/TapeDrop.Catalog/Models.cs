namespace TapeDrop.Catalog;

/// <summary>A saved backup job: a named source folder that can be written to tape on demand.</summary>
public sealed record BackupJobDef(
    long Id,
    string Name,
    string SourcePath,
    bool Verify,
    bool EjectWhenDone,
    DateTime? LastRunUtc,
    string? LastTapeLabel);

/// <summary>One file written to tape. Path is relative to the tape root, using '/' separators.</summary>
public sealed record CatalogFile(string Path, long Size, string Sha256);

/// <summary>A completed run of a copy or backup job, with every file it wrote.</summary>
public sealed record JobRecord(
    string Kind,
    string Name,
    long? BackupJobId,
    string TapeLabel,
    string DestFolder,
    DateTime StartedUtc,
    DateTime FinishedUtc,
    bool Verified,
    long Bytes,
    IReadOnlyList<CatalogFile> Files);

/// <summary>A catalog search result: which tape holds the file.</summary>
public sealed record SearchHit(string TapeLabel, string JobName, string Path, long Size, DateTime WhenUtc);
