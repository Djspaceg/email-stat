namespace EmailStat.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmailStat.Models;
using EmailStat.Services;
using Microsoft.UI.Dispatching;
using System.Collections.ObjectModel;

/// <summary>
/// Primary ViewModel.  Exposed as a property of <see cref="EmailStat.MainWindow"/>
/// and bound to via <c>x:Bind</c>.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly GmailService _gmail = new();
    private readonly DispatcherQueue _dispatcher;
    private CancellationTokenSource? _cts;

    // -------------------------------------------------------------------------
    // Observable state
    // -------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(HasData))]
    private ObservableCollection<EmailGroup> _emailGroups = [];

    [ObservableProperty]
    private string _statusMessage = "Connect to Gmail to start analysing your inbox.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isLoading;

    [ObservableProperty]
    private EmailGroup? _selectedGroup;

    [ObservableProperty]
    private string _selectedGroupInfo = string.Empty;

    [ObservableProperty]
    private double _maxMessages = 5_000;

    // Backing field for GroupByDomain: changing it re-fetches automatically.
    private bool _groupByDomain = true;
    public bool GroupByDomain
    {
        get => _groupByDomain;
        set
        {
            if (SetProperty(ref _groupByDomain, value))
            {
                OnPropertyChanged(nameof(GroupByLabelText));
                OnGroupingChanged();
            }
        }
    }

    /// <summary>Label text shown inside the group-by toggle button.</summary>
    public string GroupByLabelText => GroupByDomain ? "Domain" : "Exact address";

    public bool ShowEmptyState => !IsLoading && EmailGroups.Count == 0;
    public bool HasData       => EmailGroups.Count > 0;

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    public MainViewModel()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("MainViewModel must be created on the UI thread.");
    }

    // -------------------------------------------------------------------------
    // Commands
    // -------------------------------------------------------------------------

    /// <summary>
    /// Authenticates with Google via the embedded OAuth credentials (browser consent screen),
    /// then fetches email groups.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            IsLoading = true;
            StatusMessage = "Opening browser for Google sign-in…";

            await _gmail.AuthenticateAsync(ct);

            StatusMessage = "Fetching email data…";
            await LoadGroupsAsync(ct);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Operation cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanConnect() => !IsLoading;

    /// <summary>Re-fetches email groups with the current settings.</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (!_gmail.IsAuthenticated) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            IsLoading = true;
            StatusMessage = "Refreshing…";
            await LoadGroupsAsync(ct);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Refresh cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanRefresh() => _gmail.IsAuthenticated && !IsLoading;

    /// <summary>Cancels any in-progress fetch.</summary>
    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private async Task LoadGroupsAsync(CancellationToken ct)
    {
        var progress = new Progress<(int Fetched, int Total)>(p =>
        {
            _dispatcher.TryEnqueue(() =>
                StatusMessage = $"Fetching… {p.Fetched:N0} / {p.Total:N0} messages");
        });

        IReadOnlyList<EmailGroup> groups = await _gmail.FetchGroupsAsync(
            GroupByDomain, (int)MaxMessages, progress, ct);

        _dispatcher.TryEnqueue(() =>
        {
            EmailGroups.Clear();
            foreach (var g in groups)
                EmailGroups.Add(g);

            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(HasData));

            long total = groups.Sum(g => g.EmailCount);
            string mode = GroupByDomain ? "domain" : "address";
            StatusMessage = $"{groups.Count:N0} {mode}(s) · {total:N0} emails analysed";
        });
    }

    private void OnGroupingChanged()
    {
        if (!_gmail.IsAuthenticated || IsLoading) return;

        // Fire-and-forget: exceptions are caught inside RefreshAsync.
        _ = RefreshAsync().ContinueWith(
            t => StatusMessage = $"Error: {t.Exception!.InnerException?.Message ?? t.Exception.Message}",
            System.Threading.CancellationToken.None,
            System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted,
            System.Threading.Tasks.TaskScheduler.Default);
    }

    partial void OnSelectedGroupChanged(EmailGroup? value)
    {
        if (value is null)
        {
            SelectedGroupInfo = string.Empty;
            return;
        }

        if (value.IsDomain)
        {
            int subs = value.SubGroups.Count;
            SelectedGroupInfo =
                $"Domain: {value.Key}  ·  {value.EmailCount:N0} emails  ·  {subs} sender(s)";
        }
        else
        {
            SelectedGroupInfo = $"Sender: {value.DisplayName}  ·  {value.EmailCount:N0} emails";
        }
    }
}
