using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhoneLink.Integrations.AppleBluetooth.Capabilities;

namespace PhoneLink.Windows.Pages;

public sealed partial class MessagesPage : Page, IDisposable
{
    private CancellationTokenSource? _probeCancellation;
    private CancellationTokenSource? _inboxCancellation;
    private bool _loaded;

    public MessagesPage()
    {
        InitializeComponent();
    }

    public ObservableCollection<AppleBluetoothCapabilityListItem> Capabilities { get; } = [];
    public ObservableCollection<ClassicBluetoothPeer> Peers { get; } = [];
    public ObservableCollection<MapInboxItem> Inbox { get; } = [];

    private async void Page_Loaded(object sender, RoutedEventArgs e) { _loaded = true; await RefreshAsync(); }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void Page_Unloaded(object sender, RoutedEventArgs e) { _loaded = false; Dispose(); }

    public void SetPrivacyLocked(bool locked)
    {
        if (locked)
        {
            _probeCancellation?.Cancel();
            Peers.Clear();
            Capabilities.Clear();
            ClearInbox();
        }
        UpdateInboxButton();
    }

    public void Dispose()
    {
        _probeCancellation?.Cancel();
        _probeCancellation = null;
        Peers.Clear();
        ClearInbox();
        GC.SuppressFinalize(this);
    }

    private async Task RefreshAsync()
    {
        if (!_loaded || App.IsLocked || _probeCancellation is not null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _probeCancellation = cancellation;
        var cancellationToken = cancellation.Token;
        LoadingRing.IsActive = true;
        RefreshButton.IsEnabled = false;
        ErrorBar.IsOpen = false;
        try
        {
            var peers = await WindowsClassicPhoneProfiles.ListPairedAsync(cancellationToken);
            if (!_loaded || App.IsLocked || cancellationToken.IsCancellationRequested) return;
            Peers.Clear();
            foreach (var peer in peers) Peers.Add(peer);
            var reports = await AppServices.ProbeAppleBluetoothAsync(cancellationToken);
            if (!_loaded || App.IsLocked || cancellationToken.IsCancellationRequested) return;
            Capabilities.Clear();
            foreach (var report in reports.Where(report => report.Capability is
                AppleBluetoothCapability.MapObserveMessages or
                AppleBluetoothCapability.MapSendMessages or
                AppleBluetoothCapability.MapReplyToMessages))
            {
                Capabilities.Add(AppleBluetoothCapabilityListItem.FromReport(report));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (_loaded && !App.IsLocked)
            {
                ErrorBar.Message = "Windows could not complete the Bluetooth capability check.";
                ErrorBar.IsOpen = true;
            }
        }
        finally
        {
            if (ReferenceEquals(_probeCancellation, cancellation))
            {
                _probeCancellation = null;
                LoadingRing.IsActive = false;
                RefreshButton.IsEnabled = true;
            }
        }
    }

    private void PeerBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded) ClearInbox();
    }

    private async void ReadInboxButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_loaded || App.IsLocked || _inboxCancellation is not null || PeerBox.SelectedItem is not ClassicBluetoothPeer peer) return;
        using var cancellation = new CancellationTokenSource();
        _inboxCancellation = cancellation;
        Inbox.Clear();
        InboxStatus.Text = "Checking the selected phone for an authorized MAP inbox…";
        UpdateInboxButton();
        try
        {
            var observation = await WindowsClassicPhoneProfiles.ReadInboxAsync(peer, cancellation.Token);
            if (!_loaded || App.IsLocked || cancellation.IsCancellationRequested) return;
            InboxStatus.Text = observation.Status;
            foreach (var item in observation.Items) Inbox.Add(item);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (_loaded && !App.IsLocked && !cancellation.IsCancellationRequested)
                InboxStatus.Text = "Windows could not open the selected Bluetooth message service. No messages were displayed.";
        }
        finally { _inboxCancellation = null; UpdateInboxButton(); }
    }

    private void ClearInbox()
    {
        _inboxCancellation?.Cancel();
        Inbox.Clear();
        InboxStatus.Text = "No messages requested.";
        UpdateInboxButton();
    }

    private void UpdateInboxButton() => ReadInboxButton.IsEnabled = _loaded && !App.IsLocked &&
        _inboxCancellation is null && PeerBox.SelectedItem is ClassicBluetoothPeer;
}
