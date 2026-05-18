namespace EmailStat;

using EmailStat.Models;
using EmailStat.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

/// <summary>The application's sole window.</summary>
public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        // Window is not a FrameworkElement in WinUI 3, so DataContext must be
        // set on the root content element rather than on the Window itself.
        ((FrameworkElement)Content).DataContext = ViewModel;
        Title = "EmailStat – Gmail Treemap Visualiser";

        // Set a comfortable initial window size for a treemap visualisation.
        AppWindow.Resize(new SizeInt32(1200, 750));
    }

    // -------------------------------------------------------------------------
    // Event handlers
    // -------------------------------------------------------------------------

    private void Treemap_SelectedGroupChanged(object sender, EmailGroup? group)
    {
        ViewModel.SelectedGroup = group;
    }

    private void StatusBar_Click(object sender, RoutedEventArgs e)
    {
        var data = new DataPackage();
        data.SetText(ViewModel.StatusMessage);
        Clipboard.SetContent(data);
    }
}
