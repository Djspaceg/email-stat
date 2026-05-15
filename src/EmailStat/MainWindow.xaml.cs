namespace EmailStat;

using EmailStat.Models;
using EmailStat.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

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

    /// <summary>
    /// Opens a file picker so the user can select their Google OAuth credentials
    /// (client_secrets.json downloaded from Google Cloud Console), then triggers
    /// the authentication + fetch flow.
    /// </summary>
    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        // Show an information dialog on first use.
        if (!await ConfirmSetupAsync()) return;

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
        };
        picker.FileTypeFilter.Add(".json");

        // WinUI 3 requires associating the picker with the window handle.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, GetWindowHandle());

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        await ViewModel.ConnectCommand.ExecuteAsync(file.Path);
    }

    private void Treemap_SelectedGroupChanged(object sender, EmailGroup? group)
    {
        ViewModel.SelectedGroup = group;
    }

    private void GroupToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton button)
            groupToggleLabel.Text = button.IsChecked == true ? "Domain" : "Exact address";
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private nint GetWindowHandle() =>
        WinRT.Interop.WindowNative.GetWindowHandle(this);

    private async Task<bool> ConfirmSetupAsync()
    {
        // Re-use the existing token silently if one is already cached.
        string tokenFolder = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmailStat", "token");

        if (System.IO.Directory.Exists(tokenFolder) &&
            System.IO.Directory.GetFiles(tokenFolder).Length > 0)
            return true;

        var dlg = new ContentDialog
        {
            XamlRoot            = Content.XamlRoot,
            Title               = "Setup: Google OAuth credentials",
            PrimaryButtonText   = "Select credentials file",
            CloseButtonText     = "Cancel",
            DefaultButton       = ContentDialogButton.Primary,
            Content             = new ScrollViewer
            {
                MaxHeight = 360,
                Content   = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text =
                        "EmailStat needs read-only access to your Gmail account.\n\n" +
                        "To set up:\n" +
                        "  1. Open Google Cloud Console (console.cloud.google.com)\n" +
                        "  2. Create a project and enable the Gmail API\n" +
                        "  3. Create OAuth 2.0 credentials (Desktop application)\n" +
                        "  4. Download the credentials JSON file\n" +
                        "  5. Click 'Select credentials file' and choose that file\n\n" +
                        "Your token will be stored locally in:\n" +
                        $"  {tokenFolder}\n\n" +
                        "EmailStat only requests read-only access (gmail.readonly scope).",
                }
            }
        };

        ContentDialogResult result = await dlg.ShowAsync();
        return result == ContentDialogResult.Primary;
    }
}
