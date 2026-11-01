namespace TapeDrop.Probe;

/// <summary>Command line: <c>tapedrop-probe [command] [--option value] [--flag]</c>.</summary>
public sealed record ProbeArgs(
    string Command,
    IReadOnlyDictionary<string, string> Options,
    IReadOnlySet<string> Flags)
{
    private static readonly HashSet<string> KnownFlags =
        new(StringComparer.OrdinalIgnoreCase) { "yes", "verify", "keep", "help" };

    public static ProbeArgs Parse(string[] args)
    {
        var command = "scan";
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var i = 0;
        if (args.Length > 0 && !args[0].StartsWith("-", StringComparison.Ordinal))
        {
            command = args[0].ToLowerInvariant();
            i = 1;
        }

        for (; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length < 3)
                throw new ArgumentException($"Unexpected argument: {arg}");

            var key = arg[2..];
            if (KnownFlags.Contains(key))
            {
                flags.Add(key);
                continue;
            }

            if (i + 1 >= args.Length)
                throw new ArgumentException($"Option --{key} needs a value.");
            options[key] = args[++i];
        }

        return new ProbeArgs(command, options, flags);
    }

    public string? Get(string key) => Options.TryGetValue(key, out var value) ? value : null;

    public bool Has(string flag) => Flags.Contains(flag);
}
