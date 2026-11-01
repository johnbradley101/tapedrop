namespace TapeDrop.Core;

/// <summary>One file to copy. RelativePath uses '/' separators and has already been sanitised.</summary>
public sealed record PlannedFile(string SourcePath, string RelativePath, long Size, DateTime LastWriteUtc);

public sealed record CopyPlan(IReadOnlyList<PlannedFile> Files, long TotalBytes, IReadOnlyList<string> Warnings);

/// <summary>Turns dropped files and folders into an ordered list of files to copy.</summary>
public static class JobPlanner
{
    // Hidden files are included; system files and reparse points (junctions, symlinks) are
    // skipped so that things like $RECYCLE.BIN and junction loops are not copied.
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
    };

    public static CopyPlan Plan(IEnumerable<string> paths)
    {
        var files = new List<PlannedFile>();
        var warnings = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in paths)
        {
            var path = Path.GetFullPath(raw);

            if (File.Exists(path))
            {
                Add(path, NameSanitizer.Sanitize(Path.GetFileName(path)));
            }
            else if (Directory.Exists(path))
            {
                var rootName = NameSanitizer.Sanitize(new DirectoryInfo(path).Name);
                foreach (var file in Directory.EnumerateFiles(path, "*", Options))
                {
                    var rel = Path.GetRelativePath(path, file);
                    var parts = rel
                        .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar })
                        .Select(NameSanitizer.Sanitize);
                    Add(file, rootName + "/" + string.Join("/", parts));
                }
            }
            else
            {
                warnings.Add($"Not found: {raw}");
            }
        }

        files.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        long total = 0;
        foreach (var f in files) total += f.Size;
        return new CopyPlan(files, total, warnings);

        void Add(string source, string relativePath)
        {
            var unique = MakeUnique(relativePath);
            if (unique != relativePath)
                warnings.Add($"Renamed to avoid a clash: {relativePath} -> {unique}");
            var info = new FileInfo(source);
            files.Add(new PlannedFile(source, unique, info.Length, info.LastWriteTimeUtc));
        }

        string MakeUnique(string rel)
        {
            if (used.Add(rel)) return rel;

            var slash = rel.LastIndexOf('/');
            var dir = slash >= 0 ? rel[..(slash + 1)] : "";
            var name = rel[dir.Length..];
            var ext = Path.GetExtension(name);
            var stem = name[..^ext.Length];
            for (var n = 2; ; n++)
            {
                var candidate = $"{dir}{stem} ({n}){ext}";
                if (used.Add(candidate)) return candidate;
            }
        }
    }
}
