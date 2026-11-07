using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhoneLink.Application.Features.Calling;
using PhoneLink.Integrations.AppleBluetooth.Capabilities;

namespace PhoneLink.Windows.Pages;

public sealed partial class MessagesPage : Page, IDisposable
{
    private CancellationTokenSource? _probeCancellation;
    private CancellationTokenSource? _inboxCancellation;
    private CancellationTokenSource? _draftCancellation;
    private bool _loaded;

    public MessagesPage()
    {
        InitializeComponent();
    }

    public ObservableCollection<AppleBluetoothCapabilityListItem> Capabilities { get; } = [];
    public ObservableCollection<ClassicBluetoothPeer> Peers { get; } = [];
    public ObservableCollection<MapInboxItem> Inbox { get; } = [];

    private async void Page_Loaded(object sender, RoutedEventArgs e) { _loaded = true; UpdateDraftControls(); await RefreshAsync(); }

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
            ClearDraft();
        }
        UpdateInboxButton();
        UpdateDraftControls();
    }

    public void Dispose()
    {
        _probeCancellation?.Cancel();
        _probeCancellation = null;
        Peers.Clear();
        ClearInbox();
        ClearDraft();
        GC.SuppressFinalize(this);
    }

    private async void RefreshDraftTargetsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_loaded || App.IsLocked || _draftCancellation is not null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _draftCancellation = cancellation;
        DraftTargetBox.Items.Clear();
        UpdateDraftControls();
        DraftStatus.Text = "Checking authenticated companion sessions…";
        try
        {
            var targets = await AppServices.Current.ListConnectedMessageTargetsAsync(cancellation.Token);
            if (!IsCurrentDraftOperation(cancellation) || cancellation.IsCancellationRequested) return;
            foreach (var target in targets) DraftTargetBox.Items.Add(target);
            DraftStatus.Text = targets.Count == 0
                ? "No connected companion reported draft support. Open the updated companion on your iPhone and connect securely."
                : "Select the iPhone where you want to review this draft.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (IsCurrentDraftOperation(cancellation)) DraftStatus.Text = "Could not check companion sessions. No draft was sent.";
        }
        finally { FinishDraftOperation(cancellation); }
    }

    private void DraftTargetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (_loaded) UpdateDraftControls(); }

    private async void ReviewDraftButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_loaded || App.IsLocked || _draftCancellation is not null ||
            DraftTargetBox.SelectedItem is not CompanionDialTarget target) return;
        CompanionMessageDraft draft;
        try { draft = CompanionMessageDraft.Create(DraftNumberBox.Text, DraftBodyBox.Text); }
        catch (ArgumentException)
        {
            DraftStatus.Text = "Enter one phone number and a nonempty draft of up to 1,000 UTF-8 bytes. No draft was sent.";
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _draftCancellation = cancellation;
        UpdateDraftControls();
        DraftNumberBox.Text = DraftBodyBox.Text = string.Empty;
        DraftStatus.Text = "Waiting for your review on iPhone. Phone Link does not send the message automatically.";
        try
        {
            var result = await AppServices.Current.RequestMessageComposeAsync(target, draft, cancellation.Token);
            if (!IsCurrentDraftOperation(cancellation)) return;
            DraftStatus.Text = result.Status switch
            {
                "submitted" => "The iPhone composer reported submission. Delivery is not verified.",
                "declined" => "The draft was cancelled on iPhone.",
                "expired" => "The draft expired.",
                "rejected" or "unsupported" or "failed" => "The iPhone did not accept or complete the draft. Nothing was retried.",
                _ => "The iPhone returned no recognized draft outcome.",
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (IsCurrentDraftOperation(cancellation))
                DraftStatus.Text = "No outcome was confirmed. Check the iPhone before trying again. Nothing was retried.";
        }
        finally { FinishDraftOperation(cancellation); }
    }

    private bool IsCurrentDraftOperation(CancellationTokenSource cancellation) =>
        _loaded && !App.IsLocked && ReferenceEquals(_draftCancellation, cancellation);

    private void FinishDraftOperation(CancellationTokenSource cancellation)
    {
        if (!ReferenceEquals(_draftCancellation, cancellation)) return;
        _draftCancellation = null;
        UpdateDraftControls();
    }

    private void ClearDraft()
    {
        _draftCancellation?.Cancel();
        _draftCancellation = null;
        DraftTargetBox.Items.Clear();
        DraftNumberBox.Text = DraftBodyBox.Text = string.Empty;
        DraftStatus.Text = "Draft cleared. Open the updated iPhone companion, then find connected iPhones.";
    }

    private void UpdateDraftControls()
    {
        var ready = _loaded && !App.IsLocked && _draftCancellation is null;
        RefreshDraftTargetsButton.IsEnabled = DraftTargetBox.IsEnabled = DraftNumberBox.IsEnabled = DraftBodyBox.IsEnabled = ready;
        ReviewDraftButton.IsEnabled = ready && DraftTargetBox.SelectedItem is CompanionDialTarget;
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
