using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TapeDrop.Desktop;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;

        var tag = item.Tag as string;
        var page = tag switch
        {
            "backups" => typeof(BackupsPage),
            "catalog" => typeof(CatalogPage),
            _ => typeof(CopyPage),
        };
        ContentFrame.Navigate(page);
    }
}
