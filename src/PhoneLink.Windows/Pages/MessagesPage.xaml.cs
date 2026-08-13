using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhoneLink.Integrations.AppleBluetooth.Capabilities;

namespace PhoneLink.Windows.Pages;

public sealed partial class MessagesPage : Page, IDisposable
{
    private CancellationTokenSource? _probeCancellation;

    public MessagesPage()
    {
        InitializeComponent();
    }

    public ObservableCollection<AppleBluetoothCapabilityListItem> Capabilities { get; } = [];

    private async void Page_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void Page_Unloaded(object sender, RoutedEventArgs e) => Dispose();

    public void Dispose()
    {
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
