using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TapeDrop.Catalog;
using TapeDrop.Core;
using TapeDrop.Ltfs;

namespace TapeDrop.Desktop;

public sealed class BackupRow
{
    public BackupJobDef Def { get; }

    public BackupRow(BackupJobDef def) => Def = def;

    public string Title => Def.Name;

    public string Subtitle =>
        Def.SourcePath + "  ·  " +
        (Def.LastRunUtc is null
            ? "never run"
            : $"last run {Def.LastRunUtc.Value.ToLocalTime():g} on tape {Def.LastTapeLabel}");
}

/// <summary>Job 2: saved backup jobs. Pick a source folder once, then run it whenever a tape is in.</summary>
public sealed partial class BackupsPage : Page
{
    private CancellationTokenSource? _cts;

    public BackupsPage()
    {
        InitializeComponent();
        Reload();
    }

    private void Reload()
    {
        var rows = AppServices.Catalog.ListBackupJobs().Select(j => new BackupRow(j)).ToList();
        JobsList.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox { Header = "Name", PlaceholderText = "e.g. Projects" };
        var pathBox = new TextBox { Header = "Source folder", PlaceholderText = @"e.g. E:\Projects" };
        var browse = new Button { Content = "Browse…" };
        browse.Click += async (_, _) =>
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null) pathBox.Text = folder.Path;
        };
        var verifyBox = new CheckBox { Content = "Verify after writing", IsChecked = true };
        var ejectBox = new CheckBox { Content = "Eject when done" };

        var panel = new StackPanel { Spacing = 12, MinWidth = 420 };
        panel.Children.Add(nameBox);
        panel.Children.Add(pathBox);
        panel.Children.Add(browse);
        panel.Children.Add(verifyBox);
        panel.Children.Add(ejectBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "New backup job",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var name = nameBox.Text.Trim();
        var path = pathBox.Text.Trim();
        if (name.Length == 0 || path.Length == 0)
        {
            await AppServices.ShowMessageAsync(XamlRoot, "Missing details", "Give the job a name and a source folder.");
            return;
        }

        try
        {
            AppServices.Catalog.CreateBackupJob(name, path, verifyBox.IsChecked == true, ejectBox.IsChecked == true);
            Reload();
        }
        catch (InvalidOperationException ex)
        {
            await AppServices.ShowMessageAsync(XamlRoot, "Could not save", ex.Message);
        }
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (JobsList.SelectedItem is not BackupRow row)
        {
            await AppServices.ShowMessageAsync(XamlRoot, "Select a job", "Choose a backup job in the list first.");
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Run \"{row.Def.Name}\"",
            Content = new TextBlock
            {
                Text = $"Insert a tape in the drive, then click Run.\n\nEvery file in {row.Def.SourcePath} will be written to it.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Run",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        var job = row.Def;
        var progress = new Progress<CopyProgress>(OnProgress);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetBusy(true);

        try
        {
            var result = await Task.Run(() =>
                new BackupJobRunner(AppServices.Tape, AppServices.Catalog).RunAsync(job, progress, token));

            var ok = result.Verification is null || result.Verified;
            var message = result.Verification is null
                ? $"Backed up {result.Files.Count:N0} files to tape {result.TapeLabel}. Not verified."
                : result.Verified
                    ? $"Backed up and verified {result.Files.Count:N0} files on tape {result.TapeLabel}."
                    : $"Backed up to tape {result.TapeLabel}, but {result.Verification.Failures.Count} file(s) FAILED verification. The tape was not ejected.";
            await AppServices.ShowMessageAsync(XamlRoot, ok ? "Backup complete" : "Verification failed", message);
        }
        catch (OperationCanceledException)
        {
            await AppServices.ShowMessageAsync(XamlRoot, "Cancelled",
                "The backup was cancelled. The tape may contain a partly written folder.");
        }
        catch (SourceUnavailableException ex)
        {
            await AppServices.ShowMessageAsync(XamlRoot, "Source not available", ex.Message);
        }
        catch (InsufficientTapeSpaceException ex)
        {
            await AppServices.ShowMessageAsync(XamlRoot, "Won't fit on this tape",
                $"This backup needs {AppServices.FormatBytes(ex.RequiredBytes)} but the tape has only " +
                $"{AppServices.FormatBytes(ex.FreeBytes)} free. Insert a larger or emptier tape.");
        }
        catch (TapeNotReadyException ex)
        {
            await AppServices.ShowMessageAsync(XamlRoot, "Tape not ready", ex.Message);
        }
        catch (Exception ex)
        {
            await AppServices.ShowMessageAsync(XamlRoot, "Backup failed", ex.Message);
        }
        finally
        {
            SetBusy(false);
            Reload();
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (JobsList.SelectedItem is not BackupRow row) return;

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Delete \"{row.Def.Name}\"?",
            Content = new TextBlock
            {
                Text = "This removes the saved job only. Tapes already written, and their catalog history, are not affected.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        AppServices.Catalog.DeleteBackupJob(row.Def.Id);
        Reload();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

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
        RunButton.IsEnabled = !busy;
        DeleteButton.IsEnabled = !busy;
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
