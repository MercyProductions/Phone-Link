using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhoneLink.Application.Features.Media;
using PhoneLink.Integrations.AppleBluetooth.Audio;

namespace PhoneLink.Windows.Pages;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WinUI owns the page. Unloaded cancels operations and releases its receiver.")]
public sealed partial class AudioReceiverPage : Page
{
    private RemoteAudioSession? _session;
    private (RemoteAudioRecovery Policy, RemoteAudioCandidate Source)? _recovery;
    private CancellationTokenSource? _operationCancellation;
    private ContentDialog? _confirmation;
    private bool _loaded;
    private bool _busy;
    private bool _recovering;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public AudioReceiverPage()
    {
        InitializeComponent();
        _timer.Tick += Timer_Tick;
    }

    private void Page_Loaded(object sender, RoutedEventArgs args) { _loaded = true; _timer.Start(); UpdateButtons(); }
    private void Page_Unloaded(object sender, RoutedEventArgs args)
    {
        _loaded = false;
        _timer.Stop();
        StopForPrivacy();
        DeviceSelector.Items.Clear();
    }

    public void StopForPrivacy()
    {
        _operationCancellation?.Cancel();
        _confirmation?.Hide();
        ReleaseSession();
        if (_loaded && _recovering)
            StateText.Text = "Recovery stopped; replacement receiver cleanup is pending or unverified.";
    }

    private void ReleaseSession()
    {
        _recovery = null;
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null) { UpdateButtons(); return; }
        try
        {
            session.Dispose();
            StateText.Text = session.State == RemoteAudioState.Released
                ? "This page released its audio receiver. Other apps may still hold a connection; phone playback is unchanged."
                : "Receiver cleanup is pending or unverified. No successful release is claimed.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { StateText.Text = "Receiver cleanup could not be verified. No successful release is claimed."; }
        UpdateButtons();
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, bool recovering = false)
    {
        if (!_loaded || _busy || App.IsLocked) return;
        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        _busy = true;
        _recovering = recovering;
        BusyRing.IsActive = true;
        BusyRing.Visibility = Visibility.Visible;
        StatusBar.IsOpen = false;
        UpdateButtons();
        try { await action(cancellation.Token); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ReleaseSession();
            if (_loaded && !App.IsLocked)
                ShowStatus(recovering ? "The one-time recovery stopped, timed out or was unavailable. No further retry." :
                    "The operation stopped, timed out or was unavailable. No automatic retry was attempted.", InfoBarSeverity.Warning);
        }
        finally
        {
            _operationCancellation = null;
            _busy = false;
            _recovering = false;
            BusyRing.IsActive = false;
            BusyRing.Visibility = Visibility.Collapsed;
            UpdateButtons();
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs args) => await RunAsync(async token =>
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        DeviceSelector.Items.Clear();
        var candidates = await WindowsRemoteAudioConnection.DiscoverAsync(deadline.Token);
        if (!_loaded || token.IsCancellationRequested || App.IsLocked) return;
        foreach (var candidate in candidates) DeviceSelector.Items.Add(candidate);
        ShowStatus($"Windows found {candidates.Count} remote audio source(s). Discovery did not enable or open a receiver. Select your own phone; availability does not prove audio works.", InfoBarSeverity.Informational);
    });

    private async void OpenButton_Click(object sender, RoutedEventArgs args) => await RunAsync(async token =>
    {
        if (DeviceSelector.SelectedItem is not RemoteAudioCandidate selected || _session is not null) return;
        var allowRecovery = new CheckBox
        {
            Content = new TextBlock
            {
                Text = "If Windows reports this connection closed, try reopening this same source once. Sound may resume, even if you changed outputs on the phone.",
                TextWrapping = TextWrapping.Wrap,
            },
            IsChecked = false,
        };
        var review = new StackPanel { Spacing = 12 };
        review.Children.Add(new TextBlock
        {
            Text = $"Selected source: {selected.DisplayName}\n\nThis may immediately make the selected device's current media audible through the PC. Check speakers/headphones and volume. No audio is recorded by Phone Link. This is not cellular-call audio or microphone routing.",
            TextWrapping = TextWrapping.Wrap,
        });
        review.Children.Add(allowRecovery);
        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Receive audio from the selected source?",
            Content = review,
            PrimaryButtonText = "Enable and open receiver",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        _confirmation = confirmation;
        var result = await confirmation.ShowAsync();
        _confirmation = null;
        bool IsCurrent() => _loaded && !App.IsLocked && !token.IsCancellationRequested && ReferenceEquals(DeviceSelector.SelectedItem, selected);
        if (result != ContentDialogResult.Primary || !IsCurrent()) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var session = WindowsRemoteAudioConnection.Create(selected);
        _session = session;
        UpdateButtons();
        var outcome = await session.OpenAsync(IsCurrent, deadline.Token);
        if (!IsCurrent()) { ReleaseSession(); return; }
        if (outcome != RemoteAudioOpenOutcome.Opened)
        {
            ReleaseSession();
            ShowStatus($"Windows open result: {outcome}. This is not a diagnosis of the cause. No automatic retry.", InfoBarSeverity.Warning);
            return;
        }
        StateText.Text = $"Windows open result: {outcome}. Current connection state: {session.State}. Audio samples and audibility have not been verified.";
        if (allowRecovery.IsChecked == true)
            _recovery = (new RemoteAudioRecovery(() => WindowsRemoteAudioConnection.Create(selected), TimeSpan.FromSeconds(2)), selected);
        ShowStatus("Receiver opened. Play audio on your phone and verify sound yourself. Stop releases this app's receiver, not the phone's playback.", InfoBarSeverity.Informational);
    });

    private void StopButton_Click(object sender, RoutedEventArgs args) => StopForPrivacy();
    private void DeviceSelector_SelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateButtons();
    private async void Timer_Tick(object? sender, object args)
    {
        if (App.IsLocked) { StopForPrivacy(); return; }
        if (_session is not { } session || _busy) return;
        var state = session.State;
        StateText.Text = $"Windows audio receiver state: {state}. Sound is not measured or recorded by this app.";
        if (state == RemoteAudioState.Opened) return;
        if (state == RemoteAudioState.Closed && _recovery is { } recovery)
            await RunAsync(token => RecoverAsync(session, recovery.Policy, recovery.Source, token), recovering: true);
        else ReleaseSession();
    }

    private async Task RecoverAsync(RemoteAudioSession previous, RemoteAudioRecovery policy, RemoteAudioCandidate source,
        CancellationToken token)
    {
        _recovery = null;
        StateText.Text = "Windows reported the audio connection closed. Reopening the same source once, as approved…";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var result = await policy.TryRecoverAsync(previous, IsCurrent, deadline.Token);
        if (result.Outcome == RemoteAudioRecoveryOutcome.CleanupUnverified)
        {
            ReleaseSession();
            if (_loaded) StateText.Text = "Receiver cleanup could not be verified. No further retry.";
            return;
        }
        if (!IsCurrent() || deadline.IsCancellationRequested || result.Session is not { } replacement)
        {
            try { result.Session?.Dispose(); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                ReleaseSession();
                if (_loaded) StateText.Text = "Receiver cleanup could not be verified. No further retry.";
                return;
            }
            ReleaseSession();
            if (_loaded && !App.IsLocked && !token.IsCancellationRequested)
                ShowStatus(result.OpenOutcome is { } open
                    ? $"Recovery did not verify an open connection (Windows result: {open}). No further retry."
                    : "Recovery did not reopen the connection. No further retry.", InfoBarSeverity.Warning);
            return;
        }
        _session = replacement;
        StateText.Text = "Windows reopened the same source once. Audio samples and audibility were not verified.";
        ShowStatus("Receiver reopened. If it closes again, start a new session manually.", InfoBarSeverity.Informational);

        bool IsCurrent() => _loaded && !App.IsLocked && !token.IsCancellationRequested &&
            ReferenceEquals(_session, previous) && ReferenceEquals(DeviceSelector.SelectedItem, source);
    }

    private void UpdateButtons()
    {
        ScanButton.IsEnabled = _loaded && !_busy && _session is null && !App.IsLocked;
        DeviceSelector.IsEnabled = ScanButton.IsEnabled;
        OpenButton.IsEnabled = ScanButton.IsEnabled && DeviceSelector.SelectedItem is RemoteAudioCandidate;
        StopButton.IsEnabled = _loaded && (_busy || _session is not null);
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    { StatusBar.Message = message; StatusBar.Severity = severity; StatusBar.IsOpen = true; }
}
