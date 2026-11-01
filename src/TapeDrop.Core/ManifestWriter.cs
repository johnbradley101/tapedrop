using System.Text;
using System.Text.Json;

namespace TapeDrop.Core;

public sealed record TapeJobManifest(
    string JobName,
    string Kind,
    DateTime CreatedUtc,
    string TapeLabel,
    string DestFolder,
    string Tool,
    IReadOnlyList<CopiedFile> Files);

/// <summary>
/// Writes plain-text manifests onto the tape itself, so any machine can check the data
/// without this app. From the tape root on Linux or macOS:
///     sha256sum -c _tapedrop/&lt;job folder&gt;.sha256
/// </summary>
public static class ManifestWriter
{
    public const string FolderName = "_tapedrop";

    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Writes the .sha256 and .json files for one job. Returns the .sha256 path.</summary>
    public static string Write(string tapeRoot, string destFolder, TapeJobManifest manifest)
    {
        var dir = Path.Combine(tapeRoot, FolderName);
        Directory.CreateDirectory(dir);
        var baseName = NameSanitizer.Sanitize(destFolder);

        // sha256sum format: "<hash>  <path>" with paths relative to the tape root.
        var sb = new StringBuilder();
        foreach (var f in manifest.Files)
            sb.Append(f.Sha256).Append("  ").Append(destFolder).Append('/').Append(f.RelativePath).Append('\n');

        var shaPath = Path.Combine(dir, baseName + ".sha256");
        File.WriteAllText(shaPath, sb.ToString(), Utf8NoBom);
        File.WriteAllText(Path.Combine(dir, baseName + ".json"), JsonSerializer.Serialize(manifest, JsonOptions), Utf8NoBom);
        return shaPath;
    }
}
