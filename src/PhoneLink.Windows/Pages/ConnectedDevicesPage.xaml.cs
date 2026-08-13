using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PhoneLink.DeviceCommunication.Pairing;
using PhoneLink.Domain.Devices;
using QRCoder;
using Windows.Storage.Streams;

namespace PhoneLink.Windows.Pages;

public sealed partial class ConnectedDevicesPage : Page
{
    private readonly ObservableCollection<DeviceListItem> _devices = [];
    private LocalPairingServer? _pairingServer;
    private PairingOfferDisplay? _currentOffer;

    public ConnectedDevicesPage()
    {
        InitializeComponent();
        DevicesList.ItemsSource = _devices;
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        try
        {
            await EnsureServerAsync();
            await RefreshDevicesAsync();
        }
        catch (Exception exception)
        {
            ShowError("Phone Link could not initialize secure local storage.", exception);
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs args)
    {
        if (_pairingServer is not null)
        {
            _pairingServer.PairingApprovalRequested -= PairingServer_ApprovalRequested;
            _pairingServer.PairingCompleted -= PairingServer_PairingCompleted;
            _pairingServer = null;
        }
    }

    private async void PairButton_Click(object sender, RoutedEventArgs args)
    {
        PairButton.IsEnabled = false;
        try
        {
            var server = await EnsureServerAsync();
            CancelCurrentOffer();
            _currentOffer = server.CreateOffer(DateTimeOffset.UtcNow);
            QrImage.Source = await CreateQrImageAsync(_currentOffer.QrPayload);
            ExpiryText.Text = $"This code expires at {_currentOffer.ExpiresAt.ToLocalTime():t} and becomes invalid after its first connection attempt.";
            PairingPanel.Visibility = Visibility.Visible;
            StatusBar.IsOpen = false;
        }
        catch (Exception exception)
        {
            ShowError("A pairing code could not be created.", exception);
        }
        finally
        {
            PairButton.IsEnabled = true;
        }
    }

    private void CancelPairing_Click(object sender, RoutedEventArgs args)
    {
        CancelCurrentOffer();
        PairingPanel.Visibility = Visibility.Collapsed;
        QrImage.Source = null;
    }

    private async void RevokeButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { CommandParameter: Guid deviceId } button || XamlRoot is null)
        {
            return;
        }

        var item = _devices.FirstOrDefault(device => device.DeviceId == deviceId);
        if (item is null || !item.CanRevoke)
        {
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Revoke {item.DisplayName}?",
            Content = "The live connection will end immediately and this iPhone cannot reconnect until it is paired again. Synchronized data is retained.",
            PrimaryButtonText = "Revoke",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            await AppServices.Current.RevokeDeviceAsync(deviceId, CancellationToken.None);
            StatusBar.Title = "Device revoked";
            StatusBar.Message = $"{item.DisplayName} was disconnected and can no longer authenticate.";
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.IsOpen = true;
            await RefreshDevicesAsync();
        }
        catch (Exception exception)
        {
            ShowError("The device could not be revoked.", exception);
            button.IsEnabled = true;
        }
    }

    private async Task<LocalPairingServer> EnsureServerAsync()
    {
        if (_pairingServer is not null)
        {
            return _pairingServer;
        }

        var server = await AppServices.Current.GetPairingServerAsync(CancellationToken.None);
        server.PairingApprovalRequested += PairingServer_ApprovalRequested;
        server.PairingCompleted += PairingServer_PairingCompleted;
        _pairingServer = server;
        return server;
    }

    private void PairingServer_ApprovalRequested(
        object? sender,
        PairingApprovalRequestedEventArgs args)
    {
        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                if (XamlRoot is null)
                {
                    args.Submit(false);
                    return;
                }

                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = $"Pair {args.ClientDisplayName}?",
                    Content = CreateApprovalContent(args),
                    PrimaryButtonText = "Words match",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                };
                var result = await dialog.ShowAsync();
                args.Submit(result == ContentDialogResult.Primary);
            }))
        {
            args.Submit(false);
        }
    }

    private void PairingServer_PairingCompleted(object? sender, PairingCompletedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            CancelCurrentOffer();
            PairingPanel.Visibility = Visibility.Collapsed;
            StatusBar.Title = "iPhone paired";
            StatusBar.Message = $"{args.DisplayName} is now trusted and can reconnect securely.";
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.IsOpen = true;
            await RefreshDevicesAsync();
        });
    }

    private static StackPanel CreateApprovalContent(PairingApprovalRequestedEventArgs args)
    {
        var panel = new StackPanel { Spacing = 12, MaxWidth = 520 };
        panel.Children.Add(
            new TextBlock
            {
                Text = "Confirm only if your iPhone shows these exact six words:",
                TextWrapping = TextWrapping.Wrap,
            });
        panel.Children.Add(
            new TextBlock
            {
                Text = args.ComparisonPhrase,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = 19,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });
        return panel;
    }

    private async Task RefreshDevicesAsync()
    {
        var devices = await AppServices.Current.ListDevicesAsync(CancellationToken.None);
        _devices.Clear();
        foreach (var device in devices)
        {
            _devices.Add(
                new DeviceListItem(
                    device.Id.Value,
                    device.DisplayName,
                    $"Paired {device.PairedAt.ToLocalTime():g} · {device.Platform}",
                    device.TrustState == DeviceTrustState.Trusted ? 1 : 0,
                    device.TrustState == DeviceTrustState.Trusted));
        }

        EmptyState.Visibility = _devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DevicesList.Visibility = _devices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static async Task<BitmapImage> CreateQrImageAsync(string payload)
    {
        var png = PngByteQRCodeHelper.GetQRCode(payload, QRCodeGenerator.ECCLevel.M, 6);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        return image;
    }

    private void CancelCurrentOffer()
    {
        if (_currentOffer is null)
        {
            return;
        }

        _pairingServer?.CancelOffer(_currentOffer.SessionId);
        _currentOffer = null;
    }

    private void ShowError(string title, Exception exception)
    {
        StatusBar.Title = title;
        StatusBar.Message = exception.Message;
        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.IsOpen = true;
    }

    private sealed record DeviceListItem(
        Guid DeviceId,
        string DisplayName,
        string Detail,
        int Status,
        bool CanRevoke);
}
