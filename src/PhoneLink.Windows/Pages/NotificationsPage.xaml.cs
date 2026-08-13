using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhoneLink.Integrations.AppleBluetooth.Ancs;
using PhoneLink.Integrations.AppleBluetooth.Capabilities;

namespace PhoneLink.Windows.Pages;

public sealed partial class NotificationsPage : Page, IDisposable
{
    private CancellationTokenSource? _probeCancellation;
    private AncsNotificationSession? _ancsSession;

    public NotificationsPage()
    {
        InitializeComponent();
    }

    public ObservableCollection<AppleBluetoothCapabilityListItem> Capabilities { get; } = [];

    public ObservableCollection<AncsNotificationListItem> Notifications { get; } = [];

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        AttachSession(AppServices.Current.GetAncsSession());
        await RefreshAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        SessionStatusBar.IsOpen = false;
        try
        {
            var session = await AppServices.Current.StartAncsSessionAsync(CancellationToken.None);
            AttachSession(session);
            SessionStatusBar.Severity = InfoBarSeverity.Success;
            SessionStatusBar.Title = $"Connected to {session.DeviceName}";
            SessionStatusBar.Message = "ANCS notifications are live for this Bluetooth session.";
            SessionStatusBar.IsOpen = true;
        }
        catch (Exception exception)
        {
            SessionStatusBar.Severity = InfoBarSeverity.Error;
            SessionStatusBar.Title = "ANCS session could not start";
            SessionStatusBar.Message = exception.Message;
            SessionStatusBar.IsOpen = true;
        }
        finally
        {
            StartButton.IsEnabled = _ancsSession is null;
            StopButton.IsEnabled = _ancsSession is not null;
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        DetachSession();
        await AppServices.Current.StopAncsSessionAsync();
        Notifications.Clear();
        UpdateEmptyState();
        StartButton.IsEnabled = true;
        SessionStatusBar.Severity = InfoBarSeverity.Informational;
        SessionStatusBar.Title = "Notification session ended";
        SessionStatusBar.Message = "All session-scoped ANCS notification data was cleared.";
        SessionStatusBar.IsOpen = true;
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e) => Dispose();

    public void Dispose()
    {
        DetachSession();
        _probeCancellation?.Cancel();
        _probeCancellation?.Dispose();
        _probeCancellation = null;
        GC.SuppressFinalize(this);
    }

    private void AttachSession(AncsNotificationSession? session)
    {
        if (ReferenceEquals(_ancsSession, session))
        {
            return;
        }
        DetachSession();
        _ancsSession = session;
        if (session is null)
        {
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            return;
        }
        session.NotificationChanged += AncsSession_NotificationChanged;
        session.SessionEnded += AncsSession_SessionEnded;
        Notifications.Clear();
        foreach (var notification in session.ActiveNotifications)
        {
            Notifications.Add(AncsNotificationListItem.FromNotification(notification));
        }
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        UpdateEmptyState();
    }

    private void DetachSession()
    {
        if (_ancsSession is not null)
        {
            _ancsSession.NotificationChanged -= AncsSession_NotificationChanged;
            _ancsSession.SessionEnded -= AncsSession_SessionEnded;
            _ancsSession = null;
        }
    }

    private void AncsSession_NotificationChanged(
        object? sender,
        AncsNotificationChangedEventArgs args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            var existing = Notifications.FirstOrDefault(item => item.NotificationUid == args.NotificationUid);
            if (existing is not null)
            {
                Notifications.Remove(existing);
            }
            if (args.Notification is not null)
            {
                Notifications.Insert(0, AncsNotificationListItem.FromNotification(args.Notification));
            }
            UpdateEmptyState();
        });

    private void AncsSession_SessionEnded(object? sender, EventArgs args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            DetachSession();
            Notifications.Clear();
            UpdateEmptyState();
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            SessionStatusBar.Severity = InfoBarSeverity.Warning;
            SessionStatusBar.Title = "iPhone disconnected";
            SessionStatusBar.Message = "ANCS identifiers and notification contents were cleared as required for the ended session.";
            SessionStatusBar.IsOpen = true;
        });

    private void UpdateEmptyState()
    {
        var empty = Notifications.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        NotificationsList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task RefreshAsync()
    {
        _probeCancellation?.Cancel();
        _probeCancellation?.Dispose();
        _probeCancellation = new CancellationTokenSource();
        var cancellationToken = _probeCancellation.Token;
        LoadingRing.IsActive = true;
        RefreshButton.IsEnabled = false;
        ErrorBar.IsOpen = false;
        try
        {
            var reports = await AppServices.ProbeAppleBluetoothAsync(cancellationToken);
            Capabilities.Clear();
            foreach (var report in reports.Where(report => report.Capability is
                AppleBluetoothCapability.AncsNotifications or
                AppleBluetoothCapability.AncsActions))
            {
                Capabilities.Add(AppleBluetoothCapabilityListItem.FromReport(report));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ErrorBar.Message = exception.Message;
            ErrorBar.IsOpen = true;
        }
        finally
        {
            LoadingRing.IsActive = false;
            RefreshButton.IsEnabled = true;
        }
    }
}

public sealed class AncsNotificationListItem
{
    public uint NotificationUid { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public static AncsNotificationListItem FromNotification(AncsNotification notification) =>
        new()
        {
            NotificationUid = notification.NotificationUid,
            Title = string.IsNullOrWhiteSpace(notification.Title)
                ? "iPhone notification"
                : notification.Title,
            Source = string.IsNullOrWhiteSpace(notification.AppIdentifier)
                ? "iPhone"
                : notification.AppIdentifier,
            Body = string.Join(
                Environment.NewLine,
                new[] { notification.Subtitle, notification.Message }
                    .Where(value => !string.IsNullOrWhiteSpace(value))),
            Category = notification.Category.ToString(),
        };
}
