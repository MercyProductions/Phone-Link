using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using PhoneLink.Integrations.AppleBluetooth.Ancs;
using Windows.Security.Credentials.UI;
using Windows.Storage;

namespace PhoneLink.Windows;

public partial class App : Microsoft.UI.Xaml.Application, IDisposable
{
    private Microsoft.UI.Xaml.Window? _window;
    private SystemTrayService? _systemTray;
    private bool _appNotificationsRegistered;
    private bool _exitRequested;
    private bool _isLocked;
    private bool _verificationInProgress;
    private DateTimeOffset? _inactiveSince;
    private Task? _shutdownTask;

    private const string HelloLockSetting = "security.windowsHelloLock";
    private static readonly TimeSpan InactivityThreshold = TimeSpan.FromMinutes(5);

    public static Microsoft.UI.Xaml.Window MainWindow =>
        ((App)Current)._window ?? throw new InvalidOperationException("The main window is not available.");

    public static bool IsExitRequested => ((App)Current)._exitRequested;

    public static bool IsLocked => ((App)Current)._isLocked;

    public static bool HelloLockEnabled =>
        ApplicationData.Current.LocalSettings.Values[HelloLockSetting] as bool? ?? false;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            AppNotificationManager.Default.Register();
            _appNotificationsRegistered = true;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Windows notifications could not register: {exception.Message}");
        }

        _window = new MainWindow();
        _window.Closed += Window_Closed;
        _window.Activated += Window_Activated;
        _systemTray = new SystemTrayService((MainWindow)_window);
        _window.Activate();
        _ = StartServicesAsync();
    }

    public static async Task<bool> SetHelloLockEnabledAsync(bool enabled)
    {
        var app = (App)Current;
        if (!enabled)
        {
            ApplicationData.Current.LocalSettings.Values[HelloLockSetting] = false;
            app._inactiveSince = null;
            SetLocked(false);
            return true;
        }

        if (await UserConsentVerifier.CheckAvailabilityAsync() !=
            UserConsentVerifierAvailability.Available)
        {
            return false;
        }

        var result = await UserConsentVerifier.RequestVerificationAsync(
            "Enable Windows Hello protection for Phone Link");
        if (result != UserConsentVerificationResult.Verified)
        {
            return false;
        }

        ApplicationData.Current.LocalSettings.Values[HelloLockSetting] = true;
        app._inactiveSince = null;
        return true;
    }

    public static async Task UnlockAsync()
    {
        var app = (App)Current;
        if (!HelloLockEnabled || app._verificationInProgress)
        {
            return;
        }

        app._verificationInProgress = true;
        try
        {
            var result = await UserConsentVerifier.RequestVerificationAsync("Unlock Phone Link");
            if (result == UserConsentVerificationResult.Verified)
            {
                app._inactiveSince = null;
                SetLocked(false);
            }
        }
        finally
        {
            app._verificationInProgress = false;
        }
    }

    public static void RequestExit()
    {
        var app = (App)Current;
        if (app._exitRequested)
        {
            return;
        }

        app._exitRequested = true;
        _ = app.ExitAsync();
    }

    public static void SetLocked(bool isLocked)
    {
        var app = (App)Current;
        app._isLocked = isLocked;
        ((MainWindow)MainWindow).SetLocked(isLocked);
        if (isLocked)
        {
            _ = ClearAncsNotificationsAsync();
        }
    }

    public static async Task ClearAncsNotificationsAsync()
    {
        var app = (App)Current;
        if (!app._appNotificationsRegistered)
        {
            return;
        }

        try
        {
            await AppNotificationManager.Default.RemoveAllAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Windows notifications could not be cleared: {exception.Message}");
        }
    }

    public void Dispose()
    {
        _systemTray?.Dispose();
        _systemTray = null;
        GC.SuppressFinalize(this);
    }

    public static void ShowAncsNotification(AncsNotification notification)
    {
        var app = (App)Current;
        var isSilent = (notification.Flags & 1) != 0;
        var isPreExisting = (notification.Flags & 4) != 0;
        if (!app._appNotificationsRegistered || app._isLocked || isSilent || isPreExisting)
        {
            return;
        }

        var title = string.IsNullOrWhiteSpace(notification.Title)
            ? "iPhone notification"
            : notification.Title;
        var body = string.Join(
            Environment.NewLine,
            new[] { notification.Subtitle, notification.Message }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        var builder = new AppNotificationBuilder().AddText(Truncate(title, 256));
        if (!string.IsNullOrWhiteSpace(body))
        {
            builder.AddText(Truncate(body, 1_024));
        }
        try
        {
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Windows notification could not be shown: {exception.Message}");
        }
    }

    private static async Task StartServicesAsync()
    {
        try
        {
            await AppServices.Current.GetPairingServerAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Phone Link services failed to start: {exception}");
        }
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private async Task ExitAsync()
    {
        await ShutdownAsync();
        if (_window is not null)
        {
            _window.Activated -= Window_Activated;
            _window.Closed -= Window_Closed;
            _window.Close();
            _window = null;
        }
    }

    private Task ShutdownAsync() => _shutdownTask ??= ShutdownCoreAsync();

    private async Task ShutdownCoreAsync()
    {
        Dispose();
        await AppServices.Current.DisposeAsync();
        if (_appNotificationsRegistered)
        {
            try
            {
                AppNotificationManager.Default.Unregister();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Windows notifications could not unregister: {exception.Message}");
            }
            _appNotificationsRegistered = false;
        }
    }

    private static async void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        var app = (App)Current;
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            app._inactiveSince ??= DateTimeOffset.UtcNow;
            return;
        }

        if (HelloLockEnabled &&
            app._inactiveSince is DateTimeOffset inactiveSince &&
            DateTimeOffset.UtcNow - inactiveSince >= InactivityThreshold)
        {
            SetLocked(true);
            await UnlockAsync();
        }
    }

    private static async void Window_Closed(object sender, WindowEventArgs args)
    {
        var app = (App)Current;
        app._window?.Activated -= Window_Activated;
        await app.ShutdownAsync();
    }
}
