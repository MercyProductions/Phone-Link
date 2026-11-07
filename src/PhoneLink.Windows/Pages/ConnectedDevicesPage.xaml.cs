using System.Collections.ObjectModel;
using System.Net.NetworkInformation;
using System.Text;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PhoneLink.DeviceCommunication.Pairing;
using PhoneLink.DeviceCommunication.Discovery;
using PhoneLink.DeviceCommunication.Sessions;
using PhoneLink.Domain.Devices;
using QRCoder;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PhoneLink.Windows.Pages;

public sealed partial class ConnectedDevicesPage : Page, IDisposable
{
    private readonly ObservableCollection<DeviceListItem> _devices = [];
    private LocalPairingServer? _pairingServer;
    private PairingOfferDisplay? _currentOffer;
    private readonly DispatcherTimer _pairingExpiryTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Guid? _observedOfferId;
    private bool _pairingRequestReceived;
    private bool _isPageLoaded;
    private bool _pairingConfirmationOpen;
    private bool _savingPairingFile;
    private CancellationTokenSource? _discoveryCancellation;
    private bool _discoveryRefreshPending;

    public ConnectedDevicesPage()
    {
        InitializeComponent();
        DevicesList.ItemsSource = _devices;
        _pairingExpiryTimer.Tick += PairingExpiryTimer_Tick;
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        _isPageLoaded = true;
        NetworkChange.NetworkAddressChanged += Network_AddressChanged;
        try
        {
            RefreshNetworks();
            await EnsureServerAsync();
            await RefreshDevicesAsync();
        }
        catch (Exception exception)
        {
            ShowError("Phone Link could not initialize secure local storage.", exception);
        }
        await RefreshNearbyServicesAsync();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs args)
    {
        _isPageLoaded = false;
        Dispose();
        NetworkChange.NetworkAddressChanged -= Network_AddressChanged;
        HidePairingQr();
        _observedOfferId = null;
        PairingProgressText.Visibility = Visibility.Collapsed;
        if (_pairingServer is not null)
        {
            _pairingServer.PairingApprovalRequested -= PairingServer_ApprovalRequested;
            _pairingServer.PairingProgressChanged -= PairingServer_ProgressChanged;
            _pairingServer.PairingCompleted -= PairingServer_PairingCompleted;
            _pairingServer.SessionConnected -= PairingServer_SessionChanged;
            _pairingServer.SessionDisconnected -= PairingServer_SessionChanged;
            _pairingServer = null;
        }
    }

    private async void PairButton_Click(object sender, RoutedEventArgs args)
    {
        PairButton.IsEnabled = false;
        try
        {
            var server = await EnsureServerAsync();
            if (!_isPageLoaded || App.IsLocked) return;
            HidePairingQr();
            _currentOffer = server.CreateOffer(DateTimeOffset.UtcNow);
            var offer = _currentOffer;
            _observedOfferId = offer.SessionId;
            _pairingRequestReceived = false;
            var image = await CreateQrImageAsync(offer.QrPayload);
            if (!_isPageLoaded || App.IsLocked || _currentOffer?.SessionId != offer.SessionId)
            {
                if (_currentOffer?.SessionId == offer.SessionId) HidePairingQr();
                return;
            }
            QrImage.Source = image;
            ExpiryText.Text = $"This code expires at {_currentOffer.ExpiresAt.ToLocalTime():t} and becomes invalid after its first connection attempt.";
            PairingPanel.Visibility = Visibility.Visible;
            SavePairingFileButton.IsEnabled = !_savingPairingFile;
            ShowPairingProgress("Waiting for the companion to scan or open the offer and tap Connect securely. No matching request has reached this PC yet.");
            _pairingExpiryTimer.Start();
            StatusBar.IsOpen = false;
        }
        catch (Exception exception)
        {
            HidePairingQr();
            _observedOfferId = null;
            PairingProgressText.Visibility = Visibility.Collapsed;
            ShowError("A pairing code could not be created.", exception);
        }
        finally
        {
            PairButton.IsEnabled = true;
        }
    }

    private async void SavePairingFileButton_Click(object sender, RoutedEventArgs args)
    {
        var offer = _currentOffer;
        if (_savingPairingFile || offer is null || !CanExportOffer(offer)) return;
        _savingPairingFile = true;
        SavePairingFileButton.IsEnabled = false;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(App.MainWindow.AppWindow.Id)
            {
                SuggestedFileName = "PhoneLink-Pairing", DefaultFileExtension = ".plpair", ShowOverwritePrompt = true,
            };
            picker.FileTypeChoices.Add("Phone Link pairing offer", [".plpair"]);
            var destination = await picker.PickSaveFileAsync();
            if (destination is null || !CanExportOffer(offer)) return;
            if (!string.Equals(Path.GetExtension(destination.Path), ".plpair", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid pairing file extension.");
            var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(destination.Path)!);
            if (!CanExportOffer(offer)) return;
            var file = await folder.CreateFileAsync(Path.GetFileName(destination.Path), CreationCollisionOption.OpenIfExists);
            if (!CanExportOffer(offer)) return;
            using var transaction = await file.OpenTransactedWriteAsync();
            transaction.Stream.Size = 0;
            using var stream = transaction.Stream.AsStreamForWrite();
            var payload = Encoding.UTF8.GetBytes(offer.QrPayload);
            try
            {
                await stream.WriteAsync(payload);
                await stream.FlushAsync();
                if (!CanExportOffer(offer)) return;
                await transaction.CommitAsync();
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(payload); }
            if (CanExportOffer(offer))
                ShowPairingProgress("Pairing file saved. Transfer it only to your own iPhone, open it in Phone Link, then tap Connect securely. Keep it private and remove both copies after pairing.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (CanExportOffer(offer)) ShowPairingProgress("The pairing file could not be saved. Try a local folder or scan the code in the companion.");
        }
        finally
        {
            _savingPairingFile = false;
            SavePairingFileButton.IsEnabled = _currentOffer is { } current && CanExportOffer(current);
        }
    }

    private bool CanExportOffer(PairingOfferDisplay offer) => _isPageLoaded && !App.IsLocked && !_pairingRequestReceived &&
        _currentOffer?.SessionId == offer.SessionId && DateTimeOffset.UtcNow < offer.ExpiresAt;

    private void CancelPairing_Click(object sender, RoutedEventArgs args)
    {
        HidePairingQr();
        _observedOfferId = null;
        ShowPairingProgress("Pairing stopped. Choose Pair iPhone when you’re ready to try again.");
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
        if (!_isPageLoaded)
        {
            throw new OperationCanceledException("The devices page was closed.");
        }
        if (_pairingServer is not null)
        {
            return _pairingServer;
        }
        server.PairingApprovalRequested += PairingServer_ApprovalRequested;
        server.PairingProgressChanged += PairingServer_ProgressChanged;
        server.PairingCompleted += PairingServer_PairingCompleted;
        server.SessionConnected += PairingServer_SessionChanged;
        server.SessionDisconnected += PairingServer_SessionChanged;
        _pairingServer = server;
        return server;
    }

    private void PairingServer_ApprovalRequested(
        object? sender,
        PairingApprovalRequestedEventArgs args)
    {
        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                if (!_isPageLoaded || App.IsLocked || XamlRoot is null || _pairingConfirmationOpen ||
                    args.AttemptEnded.IsCancellationRequested || _currentOffer?.SessionId != args.SessionId)
                {
                    args.Submit(false);
                    return;
                }

                // The server has consumed this QR. Only the comparison step remains.
                HidePairingQr();
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = $"Pair {args.ClientDisplayName}?",
                    Content = CreateApprovalContent(args),
                    PrimaryButtonText = "Words match",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                };
                _pairingConfirmationOpen = true;
                var showing = true;
                using var ended = args.AttemptEnded.Register(() => DispatcherQueue.TryEnqueue(() =>
                {
                    if (showing) dialog.Hide();
                }));
                try
                {
                    var result = await dialog.ShowAsync();
                    args.Submit(_isPageLoaded && !App.IsLocked && result == ContentDialogResult.Primary);
                    if (_isPageLoaded && !App.IsLocked && args.AttemptEnded.IsCancellationRequested &&
                        DateTimeOffset.UtcNow >= args.ExpiresAt)
                    {
                        StatusBar.Title = "Pairing expired";
                        StatusBar.Message = "Choose Pair iPhone for a fresh code, then compare and confirm on both devices.";
                        StatusBar.Severity = InfoBarSeverity.Warning;
                        StatusBar.IsOpen = true;
                    }
                }
                catch (InvalidOperationException)
                {
                    args.Submit(false);
                }
                finally
                {
                    showing = false;
                    _pairingConfirmationOpen = false;
                }
            }))
        {
            args.Submit(false);
        }
    }

    private void PairingServer_PairingCompleted(object? sender, PairingCompletedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (!_isPageLoaded) { return; }
            HidePairingQr();
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
        if (!_isPageLoaded)
        {
            return;
        }
        _devices.Clear();
        foreach (var device in devices)
        {
            _devices.Add(
                new DeviceListItem(
                    device.Id.Value,
                    device.DisplayName,
                    $"Paired {device.PairedAt.ToLocalTime():g} · {device.Platform}",
                    device.TrustState != DeviceTrustState.Trusted ? "Revoked" :
                        _pairingServer?.IsConnected(device.Id) == true ? "Securely connected" : "Ready to reconnect",
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
        SavePairingFileButton.IsEnabled = false;
    }

    private void HidePairingQr()
    {
        _pairingExpiryTimer.Stop();
        CancelCurrentOffer();
        QrImage.Source = null;
        PairingPanel.Visibility = Visibility.Collapsed;
        if (PairingProgressText.Text.Length != 0) PairingProgressText.Visibility = Visibility.Visible;
    }

    private void PairingExpiryTimer_Tick(object? sender, object args)
    {
        if (App.IsLocked)
        {
            HidePairingQr();
            _observedOfferId = null;
            PairingProgressText.Visibility = Visibility.Collapsed;
        }
        else if (_currentOffer is { } offer && DateTimeOffset.UtcNow >= offer.ExpiresAt)
        {
            HidePairingQr();
            ShowPairingProgress(_pairingRequestReceived
                ? "The request reached Windows, but the offer expired. Choose Pair iPhone for a fresh code."
                : "The offer expired without a matching request reaching Windows. Scan a fresh code or transfer a new pairing file, then tap Connect securely on the same local network.");
        }
    }

    private void PairingServer_ProgressChanged(object? sender, PairingAttemptProgress args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isPageLoaded || App.IsLocked || _observedOfferId != args.SessionId) return;
            _pairingRequestReceived = true;
            SavePairingFileButton.IsEnabled = false;
            if (args.Stage is not (PairingAttemptStage.RequestReceived or PairingAttemptStage.AwaitingConfirmation)) HidePairingQr();
            ShowPairingProgress(args.Stage switch
            {
                PairingAttemptStage.RequestReceived => "A request reached Windows for this code. Checking its pairing proof…",
                PairingAttemptStage.AwaitingConfirmation => "The pairing proof verified. Compare all six words and confirm on both devices only if they match.",
                PairingAttemptStage.Trusted => "Windows saved trust. Look for Securely connected to verify an authenticated session.",
                PairingAttemptStage.Declined => "Pairing was not approved on both devices. No new trust was saved for this attempt.",
                PairingAttemptStage.Expired => "The request reached Windows, but the offer expired before pairing completed. Choose Pair iPhone for a fresh code.",
                PairingAttemptStage.Interrupted => "The request reached Windows, but the connection closed before pairing completed. Choose Pair iPhone to retry.",
                _ => "The request reached Windows, but pairing could not finish. Generate a fresh code and check the companion’s error if it happens again.",
            });
        });

    private void ShowPairingProgress(string message)
    {
        PairingProgressText.Text = message;
        LivePairingProgressText.Text = message;
        PairingProgressText.Visibility = PairingPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ShowError(string title, Exception exception)
    {
        if (!_isPageLoaded)
        {
            return;
        }
        StatusBar.Title = title;
        StatusBar.Message = exception.Message;
        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.IsOpen = true;
    }

    private sealed record DeviceListItem(
        Guid DeviceId,
        string DisplayName,
        string Detail,
        string ConnectionStatus,
        bool CanRevoke);

    private void Network_AddressChanged(object? sender, EventArgs args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isPageLoaded) { return; }
            var hadOffer = _currentOffer is not null;
            HidePairingQr();
            if (hadOffer)
            {
                _observedOfferId = null;
                PairingProgressText.Visibility = Visibility.Collapsed;
            }
            NearbyServicesList.ItemsSource = null;
            _ = RefreshNearbyServicesAsync();
            try { RefreshNetworks(); }
            catch (NetworkInformationException exception) { ShowError("Network information could not refresh.", exception); }
            if (hadOffer)
            {
                StatusBar.Title = "Network changed";
                StatusBar.Message = "Create a new pairing code using your current network addresses.";
                StatusBar.Severity = InfoBarSeverity.Informational;
                StatusBar.IsOpen = true;
            }
        });

    private void PairingServer_SessionChanged(object? sender, DeviceSessionChangedEventArgs args) =>
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (!_isPageLoaded) { return; }
            try { await RefreshDevicesAsync(); }
            catch (Exception exception) { ShowError("Connection status could not refresh.", exception); }
        });

    private void RefreshNetworks()
    {
        var networks = LocalNetworkCatalog.Capture();
        var addresses = LocalNetworkCatalog.SelectPairingAddresses(networks);
        NetworksList.ItemsSource = networks.Where(network => network.Addresses.Count > 0).Select(network => new
        {
            network.Name,
            Detail = $"{network.Type} · {network.Status} · " +
                (network.Addresses.Count == 0 ? "No assigned address" : string.Join(", ", network.Addresses)),
        }).ToArray();
        NetworkSummary.Text = addresses.Length == 0
            ? "No usable private IPv4 or IPv6 address. Connect to a local network to pair."
            : $"{addresses.Length} local pairing address(es). Bonjour advertises this PC after a device is trusted.";
    }

    public void Dispose()
    {
        _discoveryRefreshPending = false;
        _discoveryCancellation?.Cancel();
        // The asynchronous scan owns and disposes its cancellation source in finally.
        GC.SuppressFinalize(this);
    }

    private async void DiscoverButton_Click(object sender, RoutedEventArgs args) =>
        await RefreshNearbyServicesAsync();

    private async Task RefreshNearbyServicesAsync()
    {
        if (!_isPageLoaded) { return; }
        if (_discoveryCancellation is not null)
        {
            _discoveryRefreshPending = true;
            _discoveryCancellation.Cancel();
            return;
        }

        var cancellation = new CancellationTokenSource();
        _discoveryCancellation = cancellation;
        DiscoverButton.IsEnabled = false;
        DiscoverySummary.Text = "Looking for nearby services…";
        try
        {
            var services = await WindowsBonjourBrowser.DiscoverAsync(TimeSpan.FromSeconds(4), cancellation.Token);
            if (!_isPageLoaded || cancellation.IsCancellationRequested) { return; }
            NearbyServicesList.ItemsSource = services.Select(service => new
            {
                service.DisplayName,
                Detail = $"{service.Description} · Identity unverified · " +
                    (service.HostName.Length == 0 ? "Host not exposed" : service.HostName) +
                    (service.Port is int port ? $":{port}" : "") +
                    (service.Addresses.Count == 0 ? "" : $" · {string.Join(", ", service.Addresses)}"),
            }).ToArray();
            DiscoverySummary.Text = services.Count == 0
                ? "No matching services observed. Keep devices on the same local network with their sharing app active."
                : $"{services.Count} service advertisement(s) observed at {DateTimeOffset.Now:t}. This does not grant device access.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_isPageLoaded)
            {
                DiscoverySummary.Text = $"Nearby service discovery is unavailable: {exception.Message}";
            }
        }
        finally
        {
            _discoveryCancellation = null;
            cancellation.Dispose();
            if (_isPageLoaded)
            {
                DiscoverButton.IsEnabled = true;
                if (_discoveryRefreshPending)
                {
                    _discoveryRefreshPending = false;
                    _ = RefreshNearbyServicesAsync();
                }
            }
        }
    }
}
