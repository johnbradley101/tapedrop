using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TapeDrop.Probe;

/// <summary>
/// Read-only look at the machine: is the IBM LTFS software installed, does Windows see the
/// tape drive (and how many times), and which volumes exist. It changes nothing.
/// </summary>
public static class SystemScan
{
    private static readonly Regex SoftwareName =
        new("ibm|ltfs|spectrum archive|storeopen|hpe|tape|lto", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VendorFolder =
        new("ibm|ltfs|spectrum|storeopen|hpe", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static void Run(ProbeReport r)
    {
        var findings = new List<string>();

        r.Line($"TapeDrop probe, {DateTime.Now:yyyy-MM-dd HH:mm}");

        r.Section("This computer");
        r.Line("Operating system: " + RuntimeInformation.OSDescription);
        r.Line("Architecture: " + RuntimeInformation.OSArchitecture);
        r.Line("Running as administrator: " + (IsAdministrator() ? "yes" : "no"));
        if (!OperatingSystem.IsWindows())
            findings.Add("Not running on Windows, so most checks below were skipped.");

        r.Section("IBM / LTFS software");
        var software = OperatingSystem.IsWindows() ? FindInstalledSoftware() : new List<string>();
        if (software.Count == 0) r.Line("No matching installed programs found.");
        foreach (var line in software) r.Line("  " + line);

        var executables = FindExecutables();
        r.Line();
        if (executables.Count == 0) r.Line("No LTFS command-line programs found in Program Files or PATH.");
        else
        {
            r.Line("LTFS-related programs found:");
            foreach (var line in executables) r.Line("  " + line);
        }

        r.Line();
        r.Line("Related Windows services:");
        r.Line(Indent(PowerShell(
            "Get-Service | Where-Object { $_.Name -match 'ltfs|spectrum|storeopen' -or $_.DisplayName -match 'ltfs|spectrum archive|ibm.*tape' } " +
            "| Format-Table Name,Status,DisplayName -AutoSize | Out-String -Width 200", emptyText: "(none found)")));

        if (OperatingSystem.IsWindows())
        {
            if (software.Count == 0 && executables.Count == 0)
                findings.Add("IBM LTFS software was not found. Install it, then run this tool again.");
            else if (executables.Count == 0)
                findings.Add("LTFS software seems installed but no LTFS command-line programs were found. Note the install folder and send it to me.");
        }

        r.Section("Tape drives Windows can see");
        r.Line(Indent(PowerShell(
            "Get-CimInstance Win32_TapeDrive | Format-List Name,DeviceID,Manufacturer,Status,Availability | Out-String -Width 200",
            emptyText: "(no tape drive found)")));

        var countText = PowerShell("@(Get-CimInstance Win32_TapeDrive).Count");
        if (OperatingSystem.IsWindows() && int.TryParse(countText.Trim(), out var tapeCount))
        {
            r.Line($"Tape drives seen: {tapeCount}");
            if (tapeCount == 0)
                findings.Add("Windows sees no tape drive. Check the Fibre Channel card driver, cabling and zoning, and that the IBM tape driver is installed.");
            else if (tapeCount > 1)
                findings.Add($"Windows sees {tapeCount} tape drives. With one physical drive this usually means two Fibre Channel paths to it. The LTFS software may need to be pointed at one of them.");
        }

        r.Line();
        r.Line("Media changers (autoloaders / libraries):");
        r.Line(Indent(PowerShell(
            "Get-PnpDevice -Class MediumChanger -ErrorAction SilentlyContinue | Format-Table FriendlyName,Status -AutoSize | Out-String -Width 200",
            emptyText: "(none)")));

        r.Line();
        r.Line("Storage / Fibre Channel controllers:");
        r.Line(Indent(PowerShell(
            "Get-CimInstance Win32_SCSIController | Format-Table Name,Manufacturer,Status -AutoSize | Out-String -Width 200",
            emptyText: "(none found)")));

        r.Section("Volumes (drives and mounted tapes)");
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                {
                    r.Line($"  {drive.Name}  {drive.DriveType}  (not ready)");
                    continue;
                }
                r.Line($"  {drive.Name}  {drive.DriveType}  {drive.DriveFormat}  \"{drive.VolumeLabel}\"  " +
                       $"{drive.TotalSize / 1_000_000_000.0:0.0} GB total, {drive.AvailableFreeSpace / 1_000_000_000.0:0.0} GB free");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                r.Line($"  {drive.Name}  {drive.DriveType}  (could not read: {ex.Message})");
            }
        }
        r.Line("A mounted LTFS tape normally shows up here as its own drive letter. Run this tool again with a tape mounted to see it.");

        r.Section("What this means");
        if (findings.Count == 0) r.Line("  Nothing alarming found. Next: mount a spare tape by hand with the IBM tools, then run the speed tests.");
        foreach (var finding in findings) r.Line("  - " + finding);
    }

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        return IsAdministratorWindows();
    }

    [SupportedOSPlatform("windows")]
    private static bool IsAdministratorWindows()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static List<string> FindInstalledSoftware()
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows()) return found.ToList();

        foreach (var (name, version, location) in ReadUninstallEntries())
        {
            if (!SoftwareName.IsMatch(name)) continue;
            var line = name + (version.Length > 0 ? $"  v{version}" : "");
            if (!string.IsNullOrWhiteSpace(location)) line += $"  at {location}";
            found.Add(line);
        }
        return found.ToList();
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<(string Name, string Version, string? Location)> ReadUninstallEntries()
    {
        string[] roots =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };

        foreach (var root in roots)
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key is null) continue;

            foreach (var subName in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(subName);
                if (sub?.GetValue("DisplayName") is not string name) continue;
                yield return (name, sub.GetValue("DisplayVersion") as string ?? "", sub.GetValue("InstallLocation") as string);
            }
        }
    }

    private static List<string> FindExecutables()
    {
        var results = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 4,
            IgnoreInaccessible = true,
        };

        foreach (var special in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var root = Environment.GetFolderPath(special);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;

            foreach (var vendorDir in SafeDirectories(root))
            {
                if (!VendorFolder.IsMatch(Path.GetFileName(vendorDir))) continue;
                foreach (var file in SafeFiles(vendorDir, "*.exe", options))
                {
                    var name = Path.GetFileName(file);
                    if (name.Contains("ltfs", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("spectrum", StringComparison.OrdinalIgnoreCase))
                        results.Add(Describe(file));
                }
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var file in SafeFiles(dir, "*ltfs*.exe", new EnumerationOptions { IgnoreInaccessible = true }))
                results.Add(Describe(file));
        }

        return results.Take(40).ToList();
    }

    private static string Describe(string file)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(file).FileVersion;
            return string.IsNullOrWhiteSpace(version) ? file : $"{file}  (version {version})";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return file;
        }
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try { return Directory.EnumerateDirectories(root).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeFiles(string root, string pattern, EnumerationOptions options)
    {
        try { return Directory.Exists(root) ? Directory.EnumerateFiles(root, pattern, options).ToList() : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static string Indent(string text) =>
        string.Join(Environment.NewLine, text.Split('\n').Select(l => "  " + l.TrimEnd()));

    /// <summary>Runs a PowerShell snippet and returns its output; never throws.</summary>
    private static string PowerShell(string script, int timeoutSeconds = 30, string emptyText = "")
    {
        if (!OperatingSystem.IsWindows()) return "(skipped: not Windows)";

        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return "(could not start PowerShell)";

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutSeconds * 1000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return "(timed out)";
            }

            var text = (output.Result + error.Result).Trim();
            return text.Length == 0 ? emptyText : text;
        }
        catch (Exception ex)
        {
            return "(could not run PowerShell: " + ex.Message + ")";
        }
    }
}
