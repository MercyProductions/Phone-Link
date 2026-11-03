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
    private CancellationTokenSource? _operationCancellation;
    private ContentDialog? _confirmation;
    private bool _loaded;
    private bool _busy;
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
    }

    private void ReleaseSession()
    {
        var session = Interlocked.Exchange(ref _session, null);
        try
        {
            session?.Dispose();
            StateText.Text = session is null || session.State == RemoteAudioState.Released
                ? "This page released its audio receiver. Other apps may still hold a connection; phone playback is unchanged."
                : "Receiver cleanup is pending or unverified. No successful release is claimed.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { StateText.Text = "Receiver cleanup could not be verified. No successful release is claimed."; }
        UpdateButtons();
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (!_loaded || _busy || App.IsLocked) return;
        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        _busy = true;
        BusyRing.IsActive = true;
        BusyRing.Visibility = Visibility.Visible;
        StatusBar.IsOpen = false;
        UpdateButtons();
        try { await action(cancellation.Token); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ReleaseSession();
            if (_loaded && !App.IsLocked)
                ShowStatus("The operation stopped, timed out or was unavailable. No automatic retry was attempted.", InfoBarSeverity.Warning);
        }
        finally
        {
            _operationCancellation = null;
            _busy = false;
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
        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Receive audio from the selected source?",
            Content = $"Selected source: {selected.DisplayName}\n\nThis may immediately make the selected device's current media audible through the PC. Check speakers/headphones and volume. No audio is recorded by Phone Link. This is not cellular-call audio or microphone routing.",
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
        ShowStatus("Receiver opened. Play audio on your phone and verify sound yourself. Stop releases this app's receiver, not the phone's playback.", InfoBarSeverity.Informational);
    });

    private void StopButton_Click(object sender, RoutedEventArgs args) => StopForPrivacy();
    private void DeviceSelector_SelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateButtons();
    private void Timer_Tick(object? sender, object args)
    {
        if (App.IsLocked) { StopForPrivacy(); return; }
        if (_session is null || _busy) return;
        var state = _session.State;
        StateText.Text = $"Windows audio receiver state: {state}. Sound is not measured or recorded by this app.";
        if (state is not RemoteAudioState.Opened) ReleaseSession();
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
