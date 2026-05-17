namespace EmailStat;

using EmailStat.Models;
using EmailStat.ViewModels;
using Microsoft.UI.Xaml;
using Windows.Graphics;

/// <summary>The application's sole window.</summary>
public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
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
}
