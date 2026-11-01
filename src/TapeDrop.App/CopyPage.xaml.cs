using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TapeDrop.Core;
using TapeDrop.Ltfs;
using Windows.ApplicationModel.DataTransfer;

namespace TapeDrop.Desktop;

/// <summary>Job 1: drag files and folders in, copy them to tape.</summary>
public sealed partial class CopyPage : Page
{
    private readonly List<string> _staged = new();
    private CopyPlan? _plan;
    private CancellationTokenSource? _cts;
    private bool _busy;

    public CopyPage()
    {
        InitializeComponent();
        if (AppServices.IsDemoMode)
        {
            DemoBar.Message = "No tape drive is configured, so files are written to " + AppServices.DemoTapeFolder;
            DemoBar.IsOpen = true;
        }
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Add to tape job";
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (_busy || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            foreach (var item in items)
            {
                if (!string.IsNullOrEmpty(item.Path) &&
                    !_staged.Contains(item.Path, StringComparer.OrdinalIgnoreCase))
                    _staged.Add(item.Path);
            }
        }
        finally
        {
            deferral.Complete();
        }

        await RefreshPlanAsync();
    }

    private async Task RefreshPlanAsync()
    {
        StagedList.ItemsSource = _staged.ToList();
        EmptyHint.Visibility = _staged.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StagedList.Visibility = _staged.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        if (_staged.Count == 0)
        {
            _plan = null;
            SummaryText.Text = "Nothing added yet";
            CopyButton.IsEnabled = false;
            return;
        }

        SummaryText.Text = "Scanning…";
        CopyButton.IsEnabled = false;
        var paths = _staged.ToArray();
        _plan = await Task.Run(() => JobPlanner.Plan(paths));

        var text = $"{_plan.Files.Count:N0} files · {AppServices.FormatBytes(_plan.TotalBytes)}";
        if (_plan.Warnings.Count > 0)
            text += $" · {_plan.Warnings.Count} notice(s): {_plan.Warnings[0]}";
        SummaryText.Text = text;
        CopyButton.IsEnabled = _plan.Files.Count > 0 && !_busy;
    }

    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is null || _plan.Files.Count == 0) return;

        var name = NameBox.Text.Trim();
        if (name.Length == 0)
            name = System.IO.Path.GetFileName(_staged[0].TrimEnd('\\', '/'));
        if (name.Length == 0) name = "copy";

        var request = new CopyJobRequest(name, _plan, VerifyToggle.IsOn, EjectToggle.IsOn);
        var progress = new Progress<CopyProgress>(OnProgress);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetBusy(true);

        try
        {
            var result = await Task.Run(() =>
                new CopyJobRunner(AppServices.Tape, AppServices.Catalog).RunAsync(request, progress, token));

            string message;
            if (result.Verification is null)
                message = $"Copied {result.Files.Count:N0} files to tape {result.TapeLabel}. Not verified.";
            else if (result.Verified)
                message = $"Copied and verified {result.Files.Count:N0} files on tape {result.TapeLabel}.";
            else
                message = $"Copied to tape {result.TapeLabel}, but {result.Verification.Failures.Count} file(s) FAILED verification, " +
                          $"for example {result.Verification.Failures[0].RelativePath}: {result.Verification.Failures[0].Reason}. " +
                          "The tape was not ejected.";

            await AppServices.ShowMessageAsync(XamlRoot, result.Verification is null || result.Verified ? "Done" : "Verification failed", message);

            if (result.Verification is null || result.Verified)
            {
                _staged.Clear();
                await RefreshPlanAsync();
            }
        }
        catch (OperationCanceledException)
        {
            await AppServices.ShowMessageAsync(XamlRoot, "Cancelled",
                "The job was cancelled. The tape may contain a partly written folder for this job.");
        }
        catch (InsufficientTapeSpaceException ex)
        {
            await AppServices.ShowMessageAsync(XamlRoot, "Won't fit on this tape",
                $"This job needs {AppServices.FormatBytes(ex.RequiredBytes)} but the tape has only " +
                $"{AppServices.FormatBytes(ex.FreeBytes)} free. Remove some items or use another tape.");
        }
        catch (TapeNotReadyException ex)
        {
            await AppServices.ShowMessageAsync(XamlRoot, "Tape not ready", ex.Message);
        }
        catch (Exception ex)
        {
            await AppServices.ShowMessageAsync(XamlRoot, "Copy failed", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private async void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _staged.Clear();
        await RefreshPlanAsync();
    }

    private void OnProgress(CopyProgress p)
    {
        var working = (p.Phase == "Copying" || p.Phase == "Verifying") && p.TotalBytes > 0;
        JobProgress.IsIndeterminate = !working;
        if (working)
        {
            JobProgress.Maximum = p.TotalBytes;
            JobProgress.Value = p.BytesDone;
        }

        var text = p.Phase switch
        {
            "Writing index" => "Writing the tape index…",
            "Mounting" => "Mounting tape…",
            "Done" => "Finishing…",
            _ => $"{p.Phase}: {p.CurrentFile}"
        };
        if (working && p.BytesPerSecond > 0)
            text += $"   {p.BytesPerSecond / 1_000_000:0} MB/s   ({p.FilesDone:N0} of {p.FilesTotal:N0} files)";
        StatusText.Text = text;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CopyButton.IsEnabled = !busy && _plan is { Files.Count: > 0 };
        ClearButton.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        JobProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            JobProgress.IsIndeterminate = true;
            StatusText.Text = "Starting…";
        }
        else
        {
            StatusText.Text = "";
        }
    }
}
