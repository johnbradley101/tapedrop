namespace TapeDrop.Core;

/// <summary>Gives every tape a stable name for the catalog, even if it has no LTFS volume label.</summary>
public static class TapeIdentity
{
    public static string Resolve(string tapeRoot, string volumeLabel)
    {
        if (!string.IsNullOrWhiteSpace(volumeLabel))
            return volumeLabel.Trim();

        var dir = Path.Combine(tapeRoot, ManifestWriter.FolderName);
        Directory.CreateDirectory(dir);
        var idFile = Path.Combine(dir, "tape.id");

        if (File.Exists(idFile))
        {
            var existing = File.ReadAllText(idFile).Trim();
            if (existing.Length > 0) return existing;
        }

        var fresh = "TAPE-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        File.WriteAllText(idFile, fresh + "\n");
        return fresh;
    }
}
