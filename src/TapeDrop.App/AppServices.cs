using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TapeDrop.Catalog;
using TapeDrop.Ltfs;

namespace TapeDrop.Desktop;

/// <summary>
/// Shared services: the catalog database and the tape drive. If
/// %LOCALAPPDATA%\TapeDrop\settings.json exists it is used to drive the real vendor LTFS
/// package; otherwise the app runs in demo mode, treating a local folder as the tape.
/// </summary>
public static class AppServices
{
    public static string DataFolder { get; }
    public static CatalogDb Catalog { get; }
    public static ILtfsProvider Tape { get; }
    public static bool IsDemoMode { get; }
    public static string DemoTapeFolder { get; }

    static AppServices()
    {
        DataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TapeDrop");
        Directory.CreateDirectory(DataFolder);

        Catalog = new CatalogDb(Path.Combine(DataFolder, "catalog.db"));

        DemoTapeFolder = Path.Combine(DataFolder, "DemoTape");
        var settingsPath = Path.Combine(DataFolder, "settings.json");

        if (File.Exists(settingsPath))
        {
            var settings = JsonSerializer.Deserialize<ExternalLtfsSettings>(
                File.ReadAllText(settingsPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (settings is not null)
            {
                Tape = new ExternalCommandLtfsProvider(settings);
                IsDemoMode = false;
                return;
            }
        }

        Tape = new FolderLtfsProvider(DemoTapeFolder, 10L * 1024 * 1024 * 1024 * 1024, "DEMO-TAPE");
        IsDemoMode = true;
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    public static async Task ShowMessageAsync(XamlRoot root, string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }
}
