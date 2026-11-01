using System.Diagnostics;
using System.Text;

namespace TapeDrop.Ltfs;

/// <summary>
/// Settings for driving a vendor LTFS package (IBM Spectrum Archive SDE, HPE StoreOpen, ...)
/// through its command line. The exact commands depend on the vendor package and are
/// filled in from the Step 0 hardware spike, so nothing vendor-specific is hard-coded here.
/// </summary>
/// <param name="MountCommand">Command that mounts the loaded tape.</param>
/// <param name="UnmountCommand">Command that cleanly unmounts it (writes the LTFS index).</param>
/// <param name="EjectCommand">Optional command that ejects the tape.</param>
/// <param name="MountPath">Where the mounted tape appears, as a drive root such as "L:\".</param>
/// <param name="TimeoutSeconds">How long to wait for each command and for the mount to appear.</param>
public sealed record ExternalLtfsSettings(
    string MountCommand,
    string UnmountCommand,
    string? EjectCommand,
    string MountPath,
    int TimeoutSeconds = 300);

public sealed class ExternalCommandLtfsProvider : ILtfsProvider
{
    private readonly ExternalLtfsSettings _settings;

    public ExternalCommandLtfsProvider(ExternalLtfsSettings settings)
    {
        _settings = settings;
    }

    public async Task<TapeInfo> MountAsync(CancellationToken ct = default)
    {
        await RunAsync(_settings.MountCommand, ct);

        var root = Path.GetPathRoot(_settings.MountPath) ?? _settings.MountPath;
        var deadline = DateTime.UtcNow.AddSeconds(_settings.TimeoutSeconds);
        while (!Directory.Exists(_settings.MountPath))
        {
            if (DateTime.UtcNow > deadline)
                throw new TapeNotReadyException($"The tape did not appear at {_settings.MountPath} after mounting.");
            await Task.Delay(500, ct);
        }

        try
        {
            var drive = new DriveInfo(root);
            return new TapeInfo(drive.VolumeLabel ?? "", drive.TotalSize, drive.AvailableFreeSpace, _settings.MountPath);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            throw new TapeNotReadyException("Could not read the mounted tape's capacity.", ex);
        }
    }

    public Task UnmountAsync(CancellationToken ct = default) => RunAsync(_settings.UnmountCommand, ct);

    public Task EjectAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.EjectCommand))
            return Task.CompletedTask;
        return RunAsync(_settings.EjectCommand, ct);
    }

    private async Task RunAsync(string commandLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(commandLine);
        }
        else
        {
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(commandLine);
        }

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new TapeNotReadyException($"Could not run: {commandLine}", ex);
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            if (ct.IsCancellationRequested) throw;
            throw new TapeNotReadyException($"Timed out running: {commandLine}");
        }

        if (process.ExitCode != 0)
        {
            string text;
            lock (output) text = output.ToString().Trim();
            throw new TapeNotReadyException($"Command failed (exit {process.ExitCode}): {commandLine}\n{text}");
        }
    }
}
