using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhoneLink.Integrations.AppleBluetooth.Capabilities;
using PhoneLink.Integrations.AppleBluetooth.Calling;

namespace PhoneLink.Windows.Pages;

public sealed partial class CallsPage : Page, IDisposable
{
    private CancellationTokenSource? _probeCancellation;
    private CancellationTokenSource? _transportCancellation;
    private bool _loaded;

    public CallsPage()
    {
        InitializeComponent();
    }

    public ObservableCollection<AppleBluetoothCapabilityListItem> Capabilities { get; } = [];

    private async void Page_Loaded(object sender, RoutedEventArgs e) { _loaded = true; await RefreshAsync(); }

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

    private async void CallButton_Click(object sender, RoutedEventArgs e)
    {
        CallButton.IsEnabled = false;
        CallResultBar.IsOpen = false;
        try
        {
            var result = await AppServices.Current.RequestDialAsync(
                PhoneNumberBox.Text,
                CancellationToken.None);
            var launched = string.Equals(result.Status, "launched", StringComparison.Ordinal);
            CallResultBar.Severity = launched ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            CallResultBar.Title = launched ? "Sent to the Phone app" : "Call was not started";
            CallResultBar.Message = launched
                ? "The iPhone accepted the confirmed dial request."
                : result.ErrorCode ?? result.Status;
            CallResultBar.IsOpen = true;
        }
        catch (Exception exception)
        {
            CallResultBar.Severity = InfoBarSeverity.Error;
            CallResultBar.Title = "Call request failed";
            CallResultBar.Message = exception.Message;
            CallResultBar.IsOpen = true;
        }
        finally
        {
            CallButton.IsEnabled = true;
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e) => Dispose();

    public void Dispose()
    {
        _loaded = false;
        _transportCancellation?.Cancel();
        TransportCheckText.Text = "Not inspected.";
        _probeCancellation?.Cancel();
        _probeCancellation?.Dispose();
        _probeCancellation = null;
        GC.SuppressFinalize(this);
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
            foreach (var report in reports.Where(report =>
                report.Capability == AppleBluetoothCapability.PbapCallHistory))
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
