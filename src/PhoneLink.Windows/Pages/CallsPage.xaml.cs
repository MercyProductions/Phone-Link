using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhoneLink.Application.Features.Calling;
using PhoneLink.Integrations.AppleBluetooth.Capabilities;
using PhoneLink.Integrations.AppleBluetooth.Calling;

namespace PhoneLink.Windows.Pages;

public sealed partial class CallsPage : Page, IDisposable
{
    private CancellationTokenSource? _probeCancellation;
    private CancellationTokenSource? _transportCancellation;
    private CancellationTokenSource? _dialCancellation;
    private CancellationTokenSource? _targetsCancellation;
    private CancellationTokenSource? _historyCancellation;
    private long _epoch;
    private bool _loaded;

    public CallsPage()
    {
        InitializeComponent();
    }

    public ObservableCollection<AppleBluetoothCapabilityListItem> Capabilities { get; } = [];
    public ObservableCollection<ClassicBluetoothPeer> HistoryPeers { get; } = [];
    public ObservableCollection<BluetoothCallPreview> History { get; } = [];

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        await RefreshTargetsAsync();
        if (_loaded && !App.IsLocked) await RefreshAsync();
    }

    public void SetPrivacyLocked(bool locked)
    {
        if (locked) ClearPrivateState();
        UpdateDialControls();
        UpdateHistoryControls();
    }

    private void ClearPrivateState()
    {
        _epoch++;
        _dialCancellation?.Cancel();
        _targetsCancellation?.Cancel();
        _transportCancellation?.Cancel();
        _probeCancellation?.Cancel();
        ClearHistory(clearPeers: true);
        DialTargetSelector.Items.Clear();
        PhoneNumberBox.Text = string.Empty;
        CallResultBar.IsOpen = false;
        CallResultBar.Message = string.Empty;
        Capabilities.Clear();
        ErrorBar.IsOpen = false;
        TransportCheckText.Text = "Not inspected.";
        DialTargetStatus.Text = "Selection cleared. Refresh and select an authenticated iPhone. Stopping Windows waiting does not withdraw a request already delivered to the iPhone; its confirmation may remain.";
        UpdateDialControls();
    }

    private void UpdateDialControls()
    {
        var available = _loaded && !App.IsLocked && _dialCancellation is null && _targetsCancellation is null;
        DialTargetSelector.IsEnabled = available;
        RefreshTargetsButton.IsEnabled = available;
        PhoneNumberBox.IsEnabled = available;
        CallButton.IsEnabled = available && DialTargetSelector.SelectedItem is CompanionDialTarget;
    }

    private void DialTargetSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_dialCancellation is null) CallResultBar.IsOpen = false;
        UpdateDialControls();
    }

    private async void RefreshTargetsButton_Click(object sender, RoutedEventArgs e) => await RefreshTargetsAsync();

    private async Task RefreshTargetsAsync()
    {
        if (!_loaded || App.IsLocked || _targetsCancellation is not null || _dialCancellation is not null) return;
        var epoch = _epoch;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _targetsCancellation = cancellation;
        DialTargetSelector.Items.Clear();
        UpdateDialControls();
        try
        {
            var targets = await AppServices.Current.ListConnectedDialTargetsAsync(cancellation.Token);
            if (!_loaded || App.IsLocked || epoch != _epoch || cancellation.IsCancellationRequested) return;
            foreach (var target in targets) DialTargetSelector.Items.Add(target);
            DialTargetStatus.Text = targets.Count == 0
                ? "No authenticated companion session. Install, pair and connect the iPhone companion first. USB or Bluetooth presence is not enough."
                : "Select a paired identity explicitly. Nothing is selected automatically, even with one phone; reconnecting requires a fresh selection.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (_loaded && !App.IsLocked && epoch == _epoch)
                DialTargetStatus.Text = "Connected iPhones could not be listed. Refresh before selecting a target. No request was sent.";
        }
        finally { _targetsCancellation = null; UpdateDialControls(); }
    }

    private async void TransportCheckButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_loaded || App.IsLocked || _transportCancellation is not null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _transportCancellation = cancellation;
        TransportCheckButton.IsEnabled = false;
        TransportCheckText.Text = "Inspecting read-only Windows call-transport state…";
        try
        {
            var result = await WindowsCallTransportProbe.CaptureAsync(cancellation.Token);
            if (_loaded && !App.IsLocked && !cancellation.IsCancellationRequested)
                TransportCheckText.Text = result.ToDisplayText();
        }
        catch (OperationCanceledException)
        {
            if (_loaded && !App.IsLocked) TransportCheckText.Text = "Inspection stopped. No connection or call was attempted.";
        }
        finally
        {
            _transportCancellation = null;
            if (_loaded) TransportCheckButton.IsEnabled = true;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void ListHistoryPeersButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_loaded || App.IsLocked || _historyCancellation is not null) return;
        ClearHistory(clearPeers: true);
        await RunHistoryAsync(async token =>
        {
            var peers = await WindowsClassicPhoneProfiles.ListPairedAsync(token);
            if (!_loaded || App.IsLocked || token.IsCancellationRequested) return;
            foreach (var peer in peers) HistoryPeers.Add(peer);
            HistoryStatus.Text = peers.Count == 0 ? "No paired Classic Bluetooth devices found." : "Select your iPhone. No call history requested.";
        }, "Listing paired Bluetooth devices…");
    }

    private void HistoryPeerBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (_loaded) ClearHistory(clearPeers: false); }

    private async void ReadHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_loaded || App.IsLocked || _historyCancellation is not null || HistoryPeerBox.SelectedItem is not ClassicBluetoothPeer peer ||
            GetSelectedHistoryFolder() is not { } folder) return;
        History.Clear();
        await RunHistoryAsync(async token =>
        {
            var observation = await WindowsClassicPhoneProfiles.ReadCallHistoryAsync(peer, folder, token);
            if (!_loaded || App.IsLocked || token.IsCancellationRequested ||
                !ReferenceEquals(HistoryPeerBox.SelectedItem, peer) || GetSelectedHistoryFolder() != folder) return;
            HistoryStatus.Text = observation.Status;
            foreach (var entry in observation.Items) History.Add(entry);
        }, $"Requesting the phone’s permitted {folder.ToString().ToLowerInvariant()} call log…");
    }

    private BluetoothCallHistoryFolder? GetSelectedHistoryFolder() => HistoryFolderBox.SelectedIndex switch
    {
        0 => BluetoothCallHistoryFolder.Combined,
        1 => BluetoothCallHistoryFolder.Incoming,
        2 => BluetoothCallHistoryFolder.Outgoing,
        _ => null,
    };

    private async Task RunHistoryAsync(Func<CancellationToken, Task> action, string progress)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(23));
        _historyCancellation = cancellation;
        UpdateHistoryControls();
        HistoryStatus.Text = progress;
        try { await action(cancellation.Token); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (_loaded && !App.IsLocked && ReferenceEquals(_historyCancellation, cancellation))
                HistoryStatus.Text = "Windows could not complete the Bluetooth check. No call was placed.";
        }
        finally
        {
            if (ReferenceEquals(_historyCancellation, cancellation))
            {
                _historyCancellation = null;
                UpdateHistoryControls();
            }
        }
    }

    private void ClearHistory(bool clearPeers)
    {
        _historyCancellation?.Cancel();
        _historyCancellation = null;
        History.Clear();
        if (clearPeers) HistoryPeers.Clear();
        HistoryStatus.Text = "No call history requested.";
        UpdateHistoryControls();
    }

    private void UpdateHistoryControls()
    {
        var ready = _loaded && !App.IsLocked && _historyCancellation is null;
        ListHistoryPeersButton.IsEnabled = ready;
        HistoryFolderBox.IsEnabled = ready;
        ReadHistoryButton.IsEnabled = ready && HistoryPeerBox.SelectedItem is ClassicBluetoothPeer && GetSelectedHistoryFolder() is not null;
    }

    private void TransportSetupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loaded && !App.IsLocked) Frame.Navigate(typeof(CallTransportSetupPage));
    }

    private async void CallButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_loaded || App.IsLocked || _dialCancellation is not null ||
            DialTargetSelector.SelectedItem is not CompanionDialTarget target) return;
        var epoch = _epoch;
        using var cancellation = new CancellationTokenSource();
        _dialCancellation = cancellation;
        UpdateDialControls();
        CallResultBar.IsOpen = false;
        try
        {
            var result = await AppServices.Current.RequestDialAsync(
                target,
                PhoneNumberBox.Text,
                cancellation.Token);
            if (!_loaded || App.IsLocked || epoch != _epoch || cancellation.IsCancellationRequested) return;
            var launched = string.Equals(result.Status, "launched", StringComparison.Ordinal);
            CallResultBar.Severity = launched ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            CallResultBar.Title = launched ? "Sent to the Phone app" : "Launch not confirmed";
            CallResultBar.Message = launched
                ? "The iPhone accepted the confirmed dial request."
                : "The selected iPhone did not confirm a successful launch. No automatic retry was performed.";
            CallResultBar.IsOpen = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!_loaded || App.IsLocked || epoch != _epoch) return;
            CallResultBar.Severity = InfoBarSeverity.Error;
            CallResultBar.Title = "Call request failed";
            CallResultBar.Message = "Check the number, refresh and reselect the authenticated iPhone. A request already delivered may still await confirmation on that phone. No automatic retry or fallback to another device occurred.";
            CallResultBar.IsOpen = true;
        }
        finally
        {
            // Invalidate the snapshot after every attempt, including a timeout.
            DialTargetSelector.Items.Clear();
            _dialCancellation = null;
            UpdateDialControls();
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e) => Dispose();

    public void Dispose()
    {
        _loaded = false;
        ClearPrivateState();
        GC.SuppressFinalize(this);
    }

    private async Task RefreshAsync()
    {
        if (!_loaded || App.IsLocked || _probeCancellation is not null) return;
        var epoch = _epoch;
        using var cancellation = new CancellationTokenSource();
        _probeCancellation = cancellation;
        var cancellationToken = cancellation.Token;
        LoadingRing.IsActive = true;
        RefreshButton.IsEnabled = false;
        ErrorBar.IsOpen = false;
        try
        {
            var reports = await AppServices.ProbeAppleBluetoothAsync(cancellationToken);
            if (!_loaded || App.IsLocked || epoch != _epoch || cancellationToken.IsCancellationRequested) return;
            Capabilities.Clear();
            foreach (var report in reports.Where(report =>
                report.Capability == AppleBluetoothCapability.PbapCallHistory))
            {
                Capabilities.Add(AppleBluetoothCapabilityListItem.FromReport(report));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!_loaded || App.IsLocked || epoch != _epoch) return;
            ErrorBar.Message = "Bluetooth capability inspection failed. No call capability is established.";
            ErrorBar.IsOpen = true;
        }
        finally
        {
            _probeCancellation = null;
            LoadingRing.IsActive = false;
            RefreshButton.IsEnabled = true;
        }
    }
}
