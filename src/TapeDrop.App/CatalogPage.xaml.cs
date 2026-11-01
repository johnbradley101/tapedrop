using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace TapeDrop.Desktop;

public sealed class HitRow
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
}

/// <summary>Searches the local catalog: which tape holds which file.</summary>
public sealed partial class CatalogPage : Page
{
    public CatalogPage()
    {
        InitializeComponent();
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) Search();
    }

    private void Search_Click(object sender, RoutedEventArgs e) => Search();

    private void Search()
    {
        var text = SearchBox.Text.Trim();
        if (text.Length == 0) return;

        var hits = AppServices.Catalog.SearchFiles(text);
        ResultsList.ItemsSource = hits.Select(h => new HitRow
        {
            Title = h.Path,
            Subtitle = $"Tape {h.TapeLabel}  ·  {h.JobName}  ·  {AppServices.FormatBytes(h.Size)}  ·  {h.WhenUtc.ToLocalTime():d}",
        }).ToList();

        CountText.Text = hits.Count == 0 ? "No matches" : $"{hits.Count:N0} match(es)";
        HintText.Visibility = hits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
