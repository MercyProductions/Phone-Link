using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhoneLink.Integrations.AppleBluetooth.Media;

namespace PhoneLink.Windows.Pages;

public sealed partial class MediaPage : Page, IAsyncDisposable
{
    private AmsSession? _session;
    private CancellationTokenSource? _operationCancellation;
    private Task? _operation;
    private ContentDialog? _confirmation;
    private volatile bool _loaded;
    private bool _busy;
    public ObservableCollection<MediaObservationRow> Observations { get; } = [];

    public MediaPage() { InitializeComponent(); }
    private void Page_Loaded(object sender, RoutedEventArgs args) { _loaded = true; UpdateButtons(); }
    private async void Page_Unloaded(object sender, RoutedEventArgs args) => await DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        _loaded = false;
        _operationCancellation?.Cancel();
        _confirmation?.Hide();
        var operation = _operation;
        try { await StopSessionAsync(); if (operation is not null) await operation; }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        Observations.Clear();
        GC.SuppressFinalize(this);
    }

    public void StopForPrivacy()
    {
        _operationCancellation?.Cancel();
        _confirmation?.Hide();
        _ = StopForPrivacyAsync();
    }
    private async Task StopForPrivacyAsync()
    {
        try { await StopSessionAsync(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }

    private Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (!_loaded || _busy || App.IsLocked) return Task.CompletedTask;
        return _operation = ExecuteAsync(action);
    }
    private async Task ExecuteAsync(Func<CancellationToken, Task> action)
    {
        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        _busy = true;
        UpdateButtons();
        BusyRing.IsActive = true;
        BusyRing.Visibility = Visibility.Visible;
        StatusBar.IsOpen = false;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException)
        { if (_loaded && !App.IsLocked) ShowStatus("Operation cancelled or timed out. No automatic retry was sent.", InfoBarSeverity.Warning); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { if (_loaded && !App.IsLocked) ShowStatus(exception.Message, InfoBarSeverity.Warning); }
        finally
        {
            _operationCancellation = null;
            _busy = false;
            if (_loaded)
            {
                BusyRing.IsActive = false;
                BusyRing.Visibility = Visibility.Collapsed;
                UpdateButtons();
            }
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs args) => await RunAsync(async token =>
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        DeviceSelector.Items.Clear();
        var result = await AmsSession.DiscoverAsync(deadline.Token);
        if (!_loaded || token.IsCancellationRequested || App.IsLocked) return;
        foreach (var candidate in result.Candidates) DeviceSelector.Items.Add(candidate);
        ShowStatus($"Found {result.Candidates.Count} paired device(s) advertising AMS; {result.UnavailableProbes} unavailable probe(s). Select your own phone. Names are not authenticated identity, and advertising AMS does not prove characteristic access.",
            result.Candidates.Count > 0 ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
    });

    private async void StartButton_Click(object sender, RoutedEventArgs args) => await RunAsync(async token =>
    {
        if (DeviceSelector.SelectedItem is not AmsDeviceCandidate candidate) return;
        await StopSessionAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var session = await AmsSession.StartAsync(candidate, deadline.Token);
        if (!_loaded || token.IsCancellationRequested || App.IsLocked) { await session.DisposeAsync(); return; }
        _session = session;
        session.Changed += Session_Changed;
        RefreshState();
        ShowStatus("AMS setup completed. Only reported commands and observations will be shown; no playback command was sent during setup.", InfoBarSeverity.Informational);
    });

    private async void StopButton_Click(object sender, RoutedEventArgs args)
    {
        _operationCancellation?.Cancel();
        _confirmation?.Hide();
        try { await StopSessionAsync(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { if (_loaded && !App.IsLocked) ShowStatus("Media cleanup could not be fully verified.", InfoBarSeverity.Warning); }
    }
    private async Task StopSessionAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null) { session.Changed -= Session_Changed; await session.DisposeAsync(); }
        Observations.Clear();
        CommandSelector.Items.Clear();
        SessionText.Text = "Stopped. Session observations and command availability were cleared.";
        if (_loaded) UpdateButtons();
    }

    private void Session_Changed(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_loaded && !App.IsLocked && ReferenceEquals(sender, _session)) RefreshState();
    });
    private void RefreshState()
    {
        var session = _session;
        if (session is null) return;
        var snapshot = session.Capture();
        var selected = (CommandSelector.SelectedItem as MediaCommandRow)?.Command;
        if (!CommandSelector.Items.Cast<MediaCommandRow>().Select(row => row.Command).SequenceEqual(snapshot.Commands))
        {
            CommandSelector.Items.Clear();
            foreach (var command in snapshot.Commands)
            {
                var row = new MediaCommandRow(command);
                CommandSelector.Items.Add(row);
                if (command == selected) CommandSelector.SelectedItem = row;
            }
        }
        Observations.Clear();
        foreach (var observation in snapshot.Attributes) Observations.Add(new(observation));
        SessionText.Text = snapshot.Ended ? $"Session ended: {session.EndReason}. No command is available until a fresh session starts."
            : $"{session.DeviceName} · session link usable={session.IsConnected} · {(snapshot.CommandListReceived ? $"{snapshot.Commands.Count} defined command(s) advertised" : "Waiting for a supported-command list; commands disabled")} · {snapshot.Attributes.Count} observed field(s) · {session.InvalidUpdates} malformed update(s). " + string.Join(" ", session.SetupIssues);
        UpdateButtons();
    }

    private async void SendButton_Click(object sender, RoutedEventArgs args) => await RunAsync(async token =>
    {
        if (_session is not { IsConnected: true } session || CommandSelector.SelectedItem is not MediaCommandRow row) return;
        var request = session.CreateRequest(row.Command);
        _confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Confirm iPhone media command",
            Content = $"Selected device: {session.DeviceName}\nCommand: {row.Label}\n\nThis targets the phone's active media player. It can change playback, phone volume, or media preferences. A changed player/track context may invalidate this request. No automatic retry is sent, and a write does not verify its effect.",
            PrimaryButtonText = "Send command", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
        };
        ContentDialogResult decision;
        try { decision = await _confirmation.ShowAsync(); }
        finally { _confirmation = null; }
        if (decision != ContentDialogResult.Primary || token.IsCancellationRequested || !_loaded || App.IsLocked || !ReferenceEquals(session, _session)) return;
        var result = await session.SendAsync(request,
            () => _loaded && !App.IsLocked && ReferenceEquals(session, Volatile.Read(ref _session)), token);
        if (!_loaded || App.IsLocked || !ReferenceEquals(session, _session)) return;
        ShowStatus(result.Outcome switch
        {
            AmsCommandOutcome.WriteAccepted => "The write was accepted. The player may not have acted; check the phone. No retry was sent.",
            AmsCommandOutcome.SubmittedUnacknowledged => "Submitted without a device acknowledgment. Playback effect is unknown. No retry was sent.",
            AmsCommandOutcome.DeviceRejected => $"The device rejected the write (protocol error 0x{result.ProtocolError:X2}). No retry was sent.",
            AmsCommandOutcome.OutcomeUnknown => "The command may have reached the phone, but its result is unknown. Check the phone before submitting another command. No retry was sent.",
            _ => "No command was sent: availability/context changed, access was blocked, or the request was already used or cancelled before submission.",
        }, result.Outcome == AmsCommandOutcome.WriteAccepted ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
    });

    private void Selection_Changed(object sender, SelectionChangedEventArgs args) => UpdateButtons();
    private void UpdateButtons()
    {
        if (ScanButton is null) return;
        var active = _session?.IsConnected == true;
        ScanButton.IsEnabled = !_busy && !active;
        DeviceSelector.IsEnabled = !_busy && !active;
        StartButton.IsEnabled = !_busy && !active && DeviceSelector.SelectedItem is AmsDeviceCandidate;
        StopButton.IsEnabled = _busy || _session is not null;
        SendButton.IsEnabled = !_busy && active && CommandSelector.SelectedItem is MediaCommandRow;
    }
    private void ShowStatus(string message, InfoBarSeverity severity)
    { StatusBar.Message = message; StatusBar.Severity = severity; StatusBar.IsOpen = true; }
}

public sealed class MediaCommandRow(AmsRemoteCommand command)
{
    public AmsRemoteCommand Command { get; } = command;
    public string Label { get; } = command switch
    {
        AmsRemoteCommand.TogglePlayPause => "Toggle play/pause", AmsRemoteCommand.NextTrack => "Next track", AmsRemoteCommand.PreviousTrack => "Previous track",
        AmsRemoteCommand.VolumeUp => "Volume up", AmsRemoteCommand.VolumeDown => "Volume down", AmsRemoteCommand.AdvanceRepeatMode => "Cycle repeat mode",
        AmsRemoteCommand.AdvanceShuffleMode => "Cycle shuffle mode", AmsRemoteCommand.SkipForward => "Skip forward", AmsRemoteCommand.SkipBackward => "Skip backward",
        AmsRemoteCommand.LikeTrack => "Like track", AmsRemoteCommand.DislikeTrack => "Dislike track", AmsRemoteCommand.BookmarkTrack => "Bookmark track", _ => command.ToString(),
    };
}
public sealed class MediaObservationRow
{
    public MediaObservationRow(AmsAttributeObservation observation)
    {
        string[] names = observation.Entity switch
        {
            AmsEntity.Player => ["App", "Playback information", "Volume"],
            AmsEntity.Queue => ["Index", "Count", "Shuffle mode", "Repeat mode"],
            _ => ["Artist", "Album", "Title", "Duration (seconds)"],
        };
        Name = $"{observation.Entity} · {names[observation.Attribute]}";
        Value = observation.Value.Length == 0 ? "(empty value reported)" : string.Concat(observation.Value.Take(2_048).Select(character => char.IsControl(character) ? " " : character.ToString()));
        if (AmsProtocol.ParseVolume(observation) is { } volume) Value = volume.ToString("P1", CultureInfo.CurrentCulture);
        if (AmsProtocol.ParsePlayback(observation) is { } playback)
            Value = $"{new[] { "Paused", "Playing", "Rewinding", "Fast forwarding" }[playback.State]} · rate {playback.Rate.ToString(CultureInfo.InvariantCulture)} · elapsed {playback.ElapsedSeconds.ToString(CultureInfo.InvariantCulture)} seconds when received";
        Detail = $"Received {observation.ReceivedAt.LocalDateTime:T} · truncated={observation.IsTruncated} · unknown flags={observation.HasUnknownFlags}. " +
            (observation.Value.Length > 2_048 ? "Display shortened. " : "") + "A received value is an observation, not proof of a command's effect.";
        if (observation.IsComplete && observation.Entity == AmsEntity.Player &&
            (observation.Attribute == 1 && AmsProtocol.ParsePlayback(observation) is null || observation.Attribute == 2 && AmsProtocol.ParseVolume(observation) is null))
            Detail = "Numeric format invalid; showing raw text, not a validated playback/volume value. " + Detail;
    }
    public string Name { get; }
    public string Value { get; }
    public string Detail { get; }
}
