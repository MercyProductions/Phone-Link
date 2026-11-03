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
    private bool _loaded;
    private CancellationTokenSource? _actionCancellation;
    private ContentDialog? _actionDialog;
    private CancellationTokenSource? _startCancellation;

    public NotificationsPage()
    {
        InitializeComponent();
    }

    public ObservableCollection<AppleBluetoothCapabilityListItem> Capabilities { get; } = [];

    public ObservableCollection<AncsNotificationListItem> Notifications { get; } = [];

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        AttachSession(AppServices.Current.GetAncsSession());
        await RefreshAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_loaded || App.IsLocked || _startCancellation is not null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        _startCancellation = cancellation;
        StartButton.IsEnabled = false;
        SessionStatusBar.IsOpen = false;
        try
        {
            var session = await AppServices.Current.StartAncsSessionAsync(cancellation.Token);
            if (!_loaded || App.IsLocked) return;
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
            _startCancellation = null;
            if (_loaded)
            {
                StartButton.IsEnabled = _ancsSession is null;
                StopButton.IsEnabled = _ancsSession is not null;
            }
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
        _loaded = false;
        CancelPendingActionsForPrivacy();
        DetachSession();
        Notifications.Clear();
        _probeCancellation?.Cancel();
        _probeCancellation?.Dispose();
        _probeCancellation = null;
        GC.SuppressFinalize(this);
    }

    public void CancelPendingActionsForPrivacy()
    {
        _startCancellation?.Cancel();
        _actionCancellation?.Cancel();
        _actionDialog?.Hide();
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
            if (!_loaded || App.IsLocked || !ReferenceEquals(sender, _ancsSession)) return;
            Notifications.Clear();
            foreach (var notification in _ancsSession!.ActiveNotifications)
                Notifications.Add(AncsNotificationListItem.FromNotification(notification));
            UpdateEmptyState();
        });

    private void AncsSession_SessionEnded(object? sender, EventArgs args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_loaded || !ReferenceEquals(sender, _ancsSession)) return;
            _actionDialog?.Hide();
            _actionCancellation?.Cancel();
            DetachSession();
            Notifications.Clear();
            UpdateEmptyState();
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            SessionStatusBar.Severity = InfoBarSeverity.Warning;
            SessionStatusBar.Title = "Notification session ended";
            SessionStatusBar.Message = "The connection or GATT services changed. ANCS identifiers, contents and pending actions were cleared; start a fresh session to continue.";
            SessionStatusBar.IsOpen = true;
        });

    private async void PositiveAction_Click(object sender, RoutedEventArgs args) =>
        await ConfirmActionAsync((sender as Button)?.Tag as AncsNotificationListItem, AncsNotificationAction.Positive);

    private async void NegativeAction_Click(object sender, RoutedEventArgs args) =>
        await ConfirmActionAsync((sender as Button)?.Tag as AncsNotificationListItem, AncsNotificationAction.Negative);

    private async Task ConfirmActionAsync(AncsNotificationListItem? item, AncsNotificationAction action)
    {
        if (!_loaded || App.IsLocked || _actionCancellation is not null || item?.Notification is not { } notification ||
            _ancsSession is not { } session || !session.IsCurrentNotification(notification)) return;
        var label = AncsActionSender.GetLabel(notification, action);
        if (label.Length == 0) return;
        using var cancellation = new CancellationTokenSource();
        _actionCancellation = cancellation;
        try
        {
            _actionDialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Confirm iPhone notification action",
                Content = $"Device: {session.DeviceName}\nNotification: {item.Title}\n\niPhone-provided action: {label}\n\nThe iPhone determines this action's effect. It may change or dismiss the notification or affect a call. This is not a text reply. Continue only if you intend this action.",
                PrimaryButtonText = "Send action", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
            };
            var decision = await _actionDialog.ShowAsync();
            _actionDialog = null;
            if (decision != ContentDialogResult.Primary || cancellation.IsCancellationRequested || !_loaded || App.IsLocked ||
                !ReferenceEquals(session, _ancsSession)) return;
            var result = await AppServices.Current.PerformAncsActionAsync(session, notification, action, cancellation.Token);
            if (!_loaded || App.IsLocked || !ReferenceEquals(session, _ancsSession)) return;
            SessionStatusBar.Title = "Notification action";
            SessionStatusBar.Severity = result.Outcome == AncsActionOutcome.WriteAccepted ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;
            SessionStatusBar.Message = result.Outcome switch
            {
                AncsActionOutcome.WriteAccepted => "The Bluetooth write was accepted. ANCS provides no action-effect response, so completion is not verified. Check the iPhone; no retry will be sent.",
                AncsActionOutcome.DeviceRejected => $"The device rejected the write (protocol error 0x{result.ProtocolError:X2}). No retry was sent.",
                AncsActionOutcome.OutcomeUnknown => "The write may have reached the iPhone, but its result is unknown. Check the phone before doing anything else. No retry was sent.",
                _ => "No write was sent: the action was unavailable, stale, already attempted, blocked, or cancelled before submission.",
            };
            SessionStatusBar.IsOpen = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (_loaded)
            {
                SessionStatusBar.Title = "Notification action unavailable";
                SessionStatusBar.Message = "The confirmation or request could not complete. No automatic retry is made. Check the iPhone before trying another action.";
                SessionStatusBar.Severity = InfoBarSeverity.Warning;
                SessionStatusBar.IsOpen = true;
            }
        }
        finally { _actionDialog = null; _actionCancellation = null; }
    }

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
    public AncsNotification? Notification { get; set; }
    public string PositiveActionLabel => Notification is null ? string.Empty : AncsActionSender.GetLabel(Notification, AncsNotificationAction.Positive);
    public string NegativeActionLabel => Notification is null ? string.Empty : AncsActionSender.GetLabel(Notification, AncsNotificationAction.Negative);
    public Visibility PositiveActionVisibility => PositiveActionLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NegativeActionVisibility => NegativeActionLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public uint NotificationUid { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public static AncsNotificationListItem FromNotification(AncsNotification notification) =>
        new()
        {
            Notification = notification,
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
