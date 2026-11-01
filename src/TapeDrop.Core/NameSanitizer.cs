using System.Text;

namespace TapeDrop.Core;

/// <summary>
/// Makes file and folder names safe to store on a tape that must be readable on both
/// Windows and Linux: illegal characters, reserved device names and trailing dots/spaces.
/// </summary>
public static class NameSanitizer
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private const string IllegalChars = "<>:\"/\\|?*";

    public static string Sanitize(string name)
    {
        if (string.IsNullOrEmpty(name)) return "_";

        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(c < 32 || IllegalChars.IndexOf(c) >= 0 ? '_' : c);

        var s = sb.ToString().TrimEnd(' ', '.');
        if (s.Length == 0) return "_";

        // Windows reserves these names even with an extension (e.g. "nul.txt").
        var dot = s.IndexOf('.');
        var stem = dot > 0 ? s[..dot] : s;
        if (Reserved.Contains(stem)) s = "_" + s;

        return s;
    }
}
