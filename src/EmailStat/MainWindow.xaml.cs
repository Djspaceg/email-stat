namespace EmailStat;

using EmailStat.Models;
using EmailStat.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

/// <summary>The application's sole window.</summary>
public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        Title = "EmailStat – Gmail Treemap Visualiser";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Set a comfortable initial window size for a treemap visualisation.
        AppWindow.Resize(new SizeInt32(1200, 750));

        // Keep the padding columns in sync with the system caption-button insets.
        // The Loaded event covers the initial layout; Changed handles DPI, snap,
        // and tablet-mode transitions that alter the inset at runtime.
        AppTitleBar.Loaded += (_, _) => UpdateTitleBarPadding();
        AppWindow.TitleBar.Changed += (_, _) =>
            AppTitleBar.DispatcherQueue.TryEnqueue(UpdateTitleBarPadding);
    }

    // -------------------------------------------------------------------------
    // Title-bar helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Adjusts <see cref="LeftPaddingColumn"/> and <see cref="RightPaddingColumn"/>
    /// so that toolbar content is never hidden behind the system caption buttons.
    /// Insets are in physical pixels; dividing by the rasterisation scale converts
    /// them to the logical DIPs used by the layout system.
    /// </summary>
    private void UpdateTitleBarPadding()
    {
        double scale = AppTitleBar.XamlRoot?.RasterizationScale ?? 1.0;
        LeftPaddingColumn.Width  = new GridLength(AppWindow.TitleBar.LeftInset  / scale);
        RightPaddingColumn.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);
    }

    // -------------------------------------------------------------------------
    // Event handlers
    // -------------------------------------------------------------------------

    private void Treemap_SelectedGroupChanged(object sender, EmailGroup? group)
    {
        ViewModel.SelectedGroup = group;
    }
}
