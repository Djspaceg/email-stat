namespace EmailStat;

using EmailStat.Models;
using EmailStat.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

/// <summary>The application's sole window.</summary>
public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        Title = "EmailStat – Gmail Treemap Visualiser";
        ExtendsContentIntoTitleBar = true;
    }

    // -------------------------------------------------------------------------
    // Event handlers
    // -------------------------------------------------------------------------

    private void Treemap_SelectedGroupChanged(object sender, EmailGroup? group)
    {
        ViewModel.SelectedGroup = group;
    }

    private void GroupToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton button)
            groupToggleLabel.Text = button.IsChecked == true ? "Domain" : "Exact address";
    }
}
