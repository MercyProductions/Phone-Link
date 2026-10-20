using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using PhoneLink.Integrations.AppleBluetooth.Ancs;
using Windows.Security.Credentials.UI;
using Windows.Storage;
using PhoneLink.Security.Storage;
using PhoneLink.Application.Features.Security;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;

namespace PhoneLink.Windows;

public partial class App : Microsoft.UI.Xaml.Application, IDisposable
{
    private Microsoft.UI.Xaml.Window? _window;
    private SystemTrayService? _systemTray;
    private bool _appNotificationsRegistered;
    private bool _exitRequested;
    private volatile bool _isLocked;
    private bool _verificationInProgress;
    private bool _windowActive;
    private DispatcherQueueTimer? _lockTimer;
    private Task? _shutdownTask;

    private const string HelloLockSetting = "security.windowsHelloLock";
    private static readonly TimeSpan InactivityThreshold = TimeSpan.FromMinutes(5);
    private readonly InactivityLockPolicy _lockPolicy = new(TimeProvider.System, InactivityThreshold);

    public static Microsoft.UI.Xaml.Window MainWindow =>
        ((App)Current)._window ?? throw new InvalidOperationException("The main window is not available.");

    public static bool IsExitRequested => ((App)Current)._exitRequested;

    public static bool IsLocked => ((App)Current)._isLocked;

    public static bool HelloLockEnabled =>
        LocalAppStorage.HasPackageIdentity
            ? ApplicationData.Current.LocalSettings.Values[HelloLockSetting] as bool? ?? false
            : File.Exists(UnpackagedHelloPreferencePath);

    private static string UnpackagedHelloPreferencePath =>
        Path.Combine(LocalAppStorage.DirectoryPath, "windows-hello-lock.enabled");

    private static void SetHelloLockPreference(bool enabled)
    {
        if (LocalAppStorage.HasPackageIdentity)
        {
            ApplicationData.Current.LocalSettings.Values[HelloLockSetting] = enabled;
        }
        else if (enabled)
        {
            Directory.CreateDirectory(LocalAppStorage.DirectoryPath);
            File.WriteAllText(UnpackagedHelloPreferencePath, "enabled");
        }
        else
        {
            File.Delete(UnpackagedHelloPreferencePath);
        }
    }

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
        _lockPolicy.Configure(HelloLockEnabled, requireVerification: true);
        ApplyLockState();
        _lockTimer = _window.DispatcherQueue.CreateTimer();
        _lockTimer.Interval = TimeSpan.FromSeconds(1);
        _lockTimer.Tick += LockTimer_Tick;
        _lockTimer.Start();
        SystemEvents.SessionSwitch += System_SessionSwitch;
        SystemEvents.PowerModeChanged += System_PowerModeChanged;
        _systemTray = new SystemTrayService((MainWindow)_window);
        _window.Activate();
        _ = StartServicesAsync();
    }

    public static async Task<bool> SetHelloLockEnabledAsync(bool enabled)
    {
        var app = (App)Current;
        if (!enabled)
        {
            if (app._lockPolicy.IsLocked) { return false; }
            SetHelloLockPreference(false);
            app._lockPolicy.Configure(enabled: false, requireVerification: false);
            ApplyLockState();
            return true;
        }

        if (await UserConsentVerifier.CheckAvailabilityAsync() !=
            UserConsentVerifierAvailability.Available)
        {
            return false;
        }

        var result = await UserConsentVerifierInterop.RequestVerificationForWindowAsync(
            WinRT.Interop.WindowNative.GetWindowHandle(MainWindow),
            "Enable Windows Hello protection for Phone Link");
        if (result != UserConsentVerificationResult.Verified)
        {
            return false;
        }

        SetHelloLockPreference(true);
        app._lockPolicy.Configure(enabled: true, requireVerification: false);
        ApplyLockState();
        return true;
    }

    public static async Task UnlockAsync()
    {
        var app = (App)Current;
        if (!app._lockPolicy.IsLocked || app._verificationInProgress || app._exitRequested)
        {
            return;
        }

        app._verificationInProgress = true;
        var verificationVersion = app._lockPolicy.VerificationVersion;
        try
        {
            var result = await UserConsentVerifierInterop.RequestVerificationForWindowAsync(
                WinRT.Interop.WindowNative.GetWindowHandle(MainWindow), "Unlock Phone Link");
            if (result == UserConsentVerificationResult.Verified && !app._exitRequested)
            {
                app._lockPolicy.TryCompleteVerification(verificationVersion);
                ApplyLockState();
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Windows Hello verification failed: {exception.HResult:X8}");
            // Keep the privacy overlay in place; the user can retry with Unlock.
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

    public static void RecordUserActivity()
    {
        var app = (App)Current;
        app._lockPolicy.RecordActivity();
        ApplyLockState();
    }

    public static void LockForPrivacy()
    {
        var app = (App)Current;
        app._lockPolicy.RequireVerification();
        ApplyLockState();
    }

    private static void ApplyLockState()
    {
        var app = (App)Current;
        var isLocked = app._lockPolicy.IsLocked;
        if (app._isLocked == isLocked) { return; }
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
        SystemEvents.SessionSwitch -= System_SessionSwitch;
        SystemEvents.PowerModeChanged -= System_PowerModeChanged;
        if (_lockTimer is not null)
        {
            _lockTimer.Stop();
            _lockTimer.Tick -= LockTimer_Tick;
            _lockTimer = null;
        }
        _systemTray?.Dispose();
        _systemTray = null;
        GC.SuppressFinalize(this);
    }

    public static void ShowAncsNotification(AncsNotification notification, AncsNotificationSession session)
    {
        var app = (App)Current;
        var window = app._window;
        if (window is null || app._exitRequested) { return; }
        if (!window.DispatcherQueue.HasThreadAccess)
        {
            window.DispatcherQueue.TryEnqueue(() => ShowAncsNotification(notification, session));
            return;
        }
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
            // Recheck on the UI thread, so queued callbacks cannot bypass a newly
            // applied privacy lock or outlive the Bluetooth session that owns them.
            if (!ReferenceEquals(AppServices.Current.GetAncsSession(), session) || !session.IsConnected)
            {
                return;
            }
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
        app._windowActive = args.WindowActivationState != WindowActivationState.Deactivated;
        if (app._windowActive) { app._lockPolicy.RecordActivity(); }
        else { app._lockPolicy.Evaluate(); }
        ApplyLockState();
        if (app._windowActive && app._lockPolicy.IsLocked)
        {
            await UnlockAsync();
        }
    }

    private void LockTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        _lockPolicy.Evaluate();
        ApplyLockState();
    }

    private void System_SessionSwitch(object sender, SessionSwitchEventArgs args)
    {
        if (args.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.RemoteDisconnect or
            SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.SessionLogoff)
        {
            _window?.DispatcherQueue.TryEnqueue(LockForPrivacy);
        }
    }

    private void System_PowerModeChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Suspend)
        {
            _window?.DispatcherQueue.TryEnqueue(LockForPrivacy);
        }
    }

    private static async void Window_Closed(object sender, WindowEventArgs args)
    {
        var app = (App)Current;
        app._window?.Activated -= Window_Activated;
        await app.ShutdownAsync();
    }
}
