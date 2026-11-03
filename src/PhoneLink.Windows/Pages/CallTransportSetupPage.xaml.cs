using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhoneLink.Application.Features.Calling;
using PhoneLink.Integrations.AppleBluetooth.Calling;

namespace PhoneLink.Windows.Pages;

public sealed partial class CallTransportSetupPage : Page
{
    private bool _loaded;
    private bool _busy;
    private bool _privacyLocked;
    private long _epoch;
    private CancellationTokenSource? _operation;
    private ContentDialog? _confirmation;

    public CallTransportSetupPage() { InitializeComponent(); }
    private void Page_Loaded(object sender, RoutedEventArgs args) { _loaded = true; UpdateButtons(); }
    private void Page_Unloaded(object sender, RoutedEventArgs args) { _loaded = false; StopForPrivacy(); }

    public void SetPrivacyLocked(bool locked)
    {
        _privacyLocked = locked;
        if (locked) StopForPrivacy();
        else UpdateButtons();
    }

    public void StopForPrivacy()
    {
        _epoch++;
        _operation?.Cancel();
        _confirmation?.Hide();
        TransportSelector.Items.Clear();
        SetupStatus.Text = "Pending setup cancelled and local selection cleared. Windows registration/access or a connection may persist; no automatic unregister or disconnect was performed. Inspect again before another explicit action.";
        UpdateButtons();
    }

    private async Task RunAsync(Func<CancellationToken, long, Task> action)
    {
        if (!_loaded || App.IsLocked || _busy) return;
        _busy = true;
        var epoch = ++_epoch;
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        UpdateButtons();
        try { await action(cancellation.Token, epoch); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (IsCurrent(epoch, cancellation.Token))
                SetupStatus.Text = "Setup unavailable, denied or interrupted. No success is claimed; Windows state may have changed if an action already started. Check app deployment/capability and inspect registration before retrying. No automatic retry or rollback.";
        }
        finally { _operation = null; _busy = false; UpdateButtons(); }
    }

    private bool IsCurrent(long epoch, CancellationToken token) => _loaded && !_privacyLocked && !App.IsLocked && !token.IsCancellationRequested && epoch == _epoch;

    private async void DiscoverButton_Click(object sender, RoutedEventArgs args) => await RunAsync(async (token, epoch) =>
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        TransportSelector.Items.Clear();
        SetupStatus.Text = "Discovering Bluetooth phone transports without requesting access…";
        var candidates = await WindowsCallTransportControl.DiscoverAsync(deadline.Token);
        if (!IsCurrent(epoch, token)) return;
        foreach (var candidate in candidates) TransportSelector.Items.Add(candidate);
        SetupStatus.Text = $"Found {candidates.Count} Bluetooth phone transport(s). Nothing selected automatically; no access request, registration or connection attempted.";
    });

    private async void RegisterButton_Click(object sender, RoutedEventArgs args) => await ActAsync(CallTransportAction.Register);
    private async void ConnectButton_Click(object sender, RoutedEventArgs args) => await ActAsync(CallTransportAction.Connect);
    private async void UnregisterButton_Click(object sender, RoutedEventArgs args) => await ActAsync(CallTransportAction.Unregister);

    private async Task ActAsync(CallTransportAction action) => await RunAsync(async (token, epoch) =>
    {
        if (TransportSelector.SelectedItem is not CallTransportCandidate selected) return;
        var detail = action switch
        {
            CallTransportAction.Register => "Windows may request permission. If allowed, this app will register for the selected phone transport. Registration persists for this Windows user until explicitly unregistered or the device is removed. No connection or call is requested by this step.",
            CallTransportAction.Connect => "Only an already registered transport will be connected. This can affect an existing phone/audio relationship. No registration, permission request, number, dial or audio-routing command is sent. Cancellation is not a guaranteed disconnect.",
            _ => "This removes this app's persistent registration for the selected transport. It does not remove Windows Bluetooth pairing, reset phone trust or unregister another app. No automatic re-registration follows.",
        };
        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"{action} selected phone transport?",
            Content = $"Selected: {selected.DisplayName}\n\n{detail}\n\nNo success is assumed. State is checked after an action when possible. Do not proceed unless this is your phone and you accept the described change.",
            PrimaryButtonText = action.ToString(),
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        _confirmation = confirmation;
        ContentDialogResult answer;
        try { answer = await confirmation.ShowAsync(); }
        finally { _confirmation = null; }
        bool Current() => IsCurrent(epoch, token) && ReferenceEquals(TransportSelector.SelectedItem, selected);
        if (answer != ContentDialogResult.Primary || !Current())
        {
            if (Current()) SetupStatus.Text = "Confirmation cancelled. No native setup action or access request was attempted.";
            return;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var controller = WindowsCallTransportControl.Create(selected);
        var result = await controller.ExecuteAsync(action, Current, deadline.Token);
        if (Current()) SetupStatus.Text = result.ToDisplayText();
    });

    private void CancelButton_Click(object sender, RoutedEventArgs args)
    {
        _epoch++;
        _operation?.Cancel();
        _confirmation?.Hide();
        SetupStatus.Text = "Pending operation cancelled. Windows may already have changed registration/access or connected the transport. No rollback or disconnect is assumed; inspect again before retrying.";
    }
    private void TransportSelector_SelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateButtons();
    private void UpdateButtons()
    {
        var eligible = _loaded && !_privacyLocked && !App.IsLocked && !_busy;
        DiscoverButton.IsEnabled = TransportSelector.IsEnabled = eligible;
        RegisterButton.IsEnabled = ConnectButton.IsEnabled = UnregisterButton.IsEnabled = eligible && TransportSelector.SelectedItem is CallTransportCandidate;
        CancelButton.IsEnabled = _loaded && _busy;
    }
}
