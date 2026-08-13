using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhoneLink.Windows.Pages;
using PhoneLink.DeviceCommunication.Pairing;
using PhoneLink.DeviceCommunication.Sessions;
using PhoneLink.Domain.Devices;
using Windows.Graphics;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;

namespace PhoneLink.Windows;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(1240, 820));
        AppWindow.Closing += AppWindow_Closing;

        NavFrame.Navigate(typeof(MessagesPage));
        Closed += MainWindow_Closed;
        _ = InitializeDeviceStatusAsync();
    }

    private LocalPairingServer? _pairingServer;

    public void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
    }

    public void SetLocked(bool isLocked)
    {
        LockOverlay.Visibility = isLocked ? Visibility.Visible : Visibility.Collapsed;
        NavView.IsEnabled = !isLocked;
    }

    private async Task InitializeDeviceStatusAsync()
    {
        try
        {
            var server = await AppServices.Current.GetPairingServerAsync(CancellationToken.None);
            server.PairingCompleted += PairingServer_PairingCompleted;
            server.SessionConnected += PairingServer_SessionChanged;
            server.SessionDisconnected += PairingServer_SessionChanged;
            _pairingServer = server;
            await UpdateDeviceStatusAsync();
        }
        catch (Exception exception)
        {
            PaneDeviceStatus.Text = "Service unavailable";
            System.Diagnostics.Debug.WriteLine($"Device status could not initialize: {exception.Message}");
        }
    }

    private void PairingServer_PairingCompleted(object? sender, PairingCompletedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(async () => await UpdateDeviceStatusAsync());
    }

    private void PairingServer_SessionChanged(object? sender, DeviceSessionChangedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(async () => await UpdateDeviceStatusAsync());
    }

    private async Task UpdateDeviceStatusAsync()
    {
        var server = _pairingServer;
        var devices = await AppServices.Current.ListDevicesAsync(CancellationToken.None);
        var trusted = devices
            .Where(device => device.TrustState == DeviceTrustState.Trusted)
            .OrderByDescending(device => device.PairedAt)
            .ToArray();
        var connected = trusted.FirstOrDefault(device => server?.IsConnected(device.Id) == true);
        if (connected is not null)
        {
            PaneDeviceName.Text = connected.DisplayName;
            PaneDeviceStatus.Text = "Securely connected";
            PaneStatusDot.Fill = new SolidColorBrush(Colors.LimeGreen);
        }
        else if (trusted.Length > 0)
        {
            PaneDeviceName.Text = trusted[0].DisplayName;
            PaneDeviceStatus.Text = "Ready to reconnect";
            PaneStatusDot.Fill = new SolidColorBrush(Colors.Goldenrod);
        }
        else
        {
            PaneDeviceName.Text = "iPhone";
            PaneDeviceStatus.Text = "Not paired";
            PaneStatusDot.Fill = new SolidColorBrush(Colors.Gray);
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        AppWindow.Closing -= AppWindow_Closing;
        if (_pairingServer is null)
        {
            return;
        }
        _pairingServer.PairingCompleted -= PairingServer_PairingCompleted;
        _pairingServer.SessionConnected -= PairingServer_SessionChanged;
        _pairingServer.SessionDisconnected -= PairingServer_SessionChanged;
        _pairingServer = null;
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (App.IsExitRequested)
        {
            return;
        }

        args.Cancel = true;
        sender.Hide();
    }

    private async void UnlockButton_Click(object sender, RoutedEventArgs args) =>
        await App.UnlockAsync();

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        if (NavFrame.CanGoBack)
        {
            NavFrame.GoBack();
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavFrame.Navigate(typeof(SettingsPage));
        }
        else if (args.SelectedItem is NavigationViewItem item)
        {
            switch (item.Tag)
            {
                case "messages":
                    NavFrame.Navigate(typeof(MessagesPage));
                    break;
                case "contacts":
                    NavFrame.Navigate(typeof(ContactsPage));
                    break;
                case "photos":
                    NavFrame.Navigate(typeof(PhotosPage));
                    break;
                case "calls":
                    NavFrame.Navigate(typeof(CallsPage));
                    break;
                case "notifications":
                    NavFrame.Navigate(typeof(NotificationsPage));
                    break;
                case "devices":
                    NavFrame.Navigate(typeof(ConnectedDevicesPage));
                    break;
                case "about":
                    NavFrame.Navigate(typeof(AboutPage));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown navigation item tag: {item.Tag}");
            }
        }
    }
}
