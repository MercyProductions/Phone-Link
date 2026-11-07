using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhoneLink.Application.Features.Diagnostics;
using PhoneLink.DeviceCommunication.Diagnostics;
using PhoneLink.Domain.Devices;
using PhoneLink.Security.Storage;
using Windows.ApplicationModel.DataTransfer;
using Windows.Management.Deployment;
using Microsoft.Windows.Storage.Pickers;

namespace PhoneLink.Windows.Pages;

public sealed partial class DiagnosticsPage : Page
{
    private CancellationTokenSource? _scanCancellation;
    private ConnectionDiagnosticReport? _report;
    private readonly ConnectionDiagnosticHistory _connectionHistory = new();
    private ConnectionDiagnosticComparison? _comparison;
    private CancellationTokenSource? _motionCancellation;
    private ImportedMotionReport? _motionReport;
    private long _motionEpoch;
    private CancellationTokenSource? _crashCancellation;
    private IphoneCrashAnalysis? _crashAnalysis;
    private bool _loaded;
    private WindowsUsbPresenceMonitor? _presenceMonitor;
    private CancellationTokenSource? _presenceCancellation;
    private DispatcherTimer? _presenceTimer;
    private long _presenceEpoch;
    private long _presenceStartedAt;
    private UsbConnectionRefresh? _presenceRefresh;
    private bool _usbRefreshInProgress;
    public ObservableCollection<DiagnosticFinding> Findings { get; } = [];

    public DiagnosticsPage() { InitializeComponent(); }

    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        _loaded = true;
        await ScanAsync();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs args)
    {
        ClearMotionForPrivacy();
        ClearConnectionHistory();
        StopPresence();
        ClearCompanionHealthForPrivacy();
        _loaded = false;
        _scanCancellation?.Cancel();
        _crashCancellation?.Cancel();
        _crashAnalysis = null;
        CrashAnalysisText.Text = string.Empty;
        CrashCopyButton.IsEnabled = false;
        PhoneInfoText.Text = string.Empty;
        RecoveryText.Text = string.Empty;
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs args) => await ScanAsync();

    public void ClearCompanionHealthForPrivacy() => CompanionHealthText.Text = "No companion health snapshot displayed.";

    public void SetPrivacyLocked(bool locked)
    {
        if (!locked) return; // Monitoring requires a fresh explicit start after unlocking.
        ClearMotionForPrivacy();
        ClearConnectionHistory();
        StopPresence();
        _scanCancellation?.Cancel();
        _crashCancellation?.Cancel();
        ClearCompanionHealthForPrivacy();
        _crashAnalysis = null;
        _report = null;
        Findings.Clear();
        CrashAnalysisText.Text = PhoneInfoText.Text = RecoveryText.Text = string.Empty;
        SnapshotText.Text = "No connection snapshot displayed.";
        CopyButton.IsEnabled = CrashCopyButton.IsEnabled = false;
    }

    private void ClearConnectionHistory()
    {
        _connectionHistory.Clear();
        _comparison = null;
        ComparisonText.Text = "No comparison retained. Run connection checks twice to compare aggregate observations.";
        ComparisonJsonButton.IsEnabled = false;
        _report = null;
        CopyButton.IsEnabled = false;
        Findings.Clear();
    }

    private void ClearMotionForPrivacy()
    {
        _motionEpoch++;
        _motionCancellation?.Cancel();
        _motionReport = null;
        MotionReportText.Text = "No imported motion summary displayed. Imported files are not authenticated live phone data.";
        MotionCopyButton.IsEnabled = false;
    }

    private async void MotionImportButton_Click(object sender, RoutedEventArgs args)
    {
        if (!_loaded || App.IsLocked || _motionCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _motionCancellation = cancellation;
        var epoch = ++_motionEpoch;
        MotionImportButton.IsEnabled = MotionCopyButton.IsEnabled = false;
        _motionReport = null;
        MotionReportText.Text = "Select a companion motion-summary JSON file. The source will remain unverified.";
        try
        {
            var picker = new FileOpenPicker(App.MainWindow.AppWindow.Id);
            picker.FileTypeFilter.Add(".json");
            var file = await picker.PickSingleFileAsync();
            if (file is null || !_loaded || App.IsLocked || cancellation.IsCancellationRequested || epoch != _motionEpoch) return;
            cancellation.CancelAfter(TimeSpan.FromSeconds(20));
            await using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > ImportedMotionReportParser.MaximumBytes) throw new InvalidDataException("Summary is oversized.");
            var report = await ImportedMotionReportParser.ParseAsync(stream, cancellation.Token);
            if (!_loaded || App.IsLocked || cancellation.IsCancellationRequested || epoch != _motionEpoch) return;
            _motionReport = report;
            MotionReportText.Text = report.ToDisplayText();
            MotionCopyButton.IsEnabled = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (_loaded && !App.IsLocked && epoch == _motionEpoch)
                MotionReportText.Text = "Import not completed. Choose a schema-1 companion motion summary up to 8 KiB. Unsupported, ambiguous or inconsistent data was not accepted. No repair or source-file change occurred.";
        }
        finally
        {
            _motionCancellation = null;
            if (_loaded) MotionImportButton.IsEnabled = true;
        }
    }

    private void MotionCopyButton_Click(object sender, RoutedEventArgs args)
    {
        if (!_loaded || App.IsLocked || _motionReport is null) return;
        try
        {
            var data = new DataPackage();
            data.SetText(_motionReport.ToDisplayText());
            Clipboard.SetContent(data);
            ShowStatus("Imported summary copied", "Only displayed fixed labels and numeric summaries were copied, with the unverified-origin warning. The source file and its arbitrary metadata were not copied.", InfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { ShowStatus("Clipboard unavailable", "The imported summary remains on this page.", InfoBarSeverity.Warning); }
    }

    private async void PresenceButton_Click(object sender, RoutedEventArgs args)
    {
        if (_presenceMonitor is not null || _presenceCancellation is not null) { StopPresence(); return; }
        if (!_loaded || App.IsLocked) return;
        var epoch = ++_presenceEpoch;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _presenceCancellation = cancellation;
        PresenceButton.Content = "Stop live USB monitor";
        PresenceText.Text = "Enumerating current Apple USB nodes; initial enumeration is not a reconnect event…";
        try
        {
            var monitor = await WindowsUsbPresenceMonitor.StartAsync(cancellation.Token);
            if (!_loaded || App.IsLocked || cancellation.IsCancellationRequested || epoch != _presenceEpoch) { monitor.Dispose(); return; }
            _presenceMonitor = monitor;
            _presenceRefresh = new UsbConnectionRefresh();
            _presenceStartedAt = Stopwatch.GetTimestamp();
            _presenceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _presenceTimer.Tick += PresenceTimer_Tick;
            _presenceTimer.Start();
            RenderPresence();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (_loaded && !App.IsLocked && epoch == _presenceEpoch)
            {
                StopPresence();
                PresenceText.Text = "USB monitoring is unavailable or timed out. Presence is unknown, not zero. Run connection checks or explicitly retry; no repair was attempted.";
            }
        }
        finally { if (ReferenceEquals(_presenceCancellation, cancellation)) _presenceCancellation = null; }
    }

    private async void PresenceTimer_Tick(object? sender, object args)
    {
        RenderPresence();
        if (_presenceMonitor is { } monitor && _presenceRefresh is { } refresh &&
            refresh.TryStart(monitor.Snapshot, Stopwatch.GetElapsedTime(_presenceStartedAt), _scanCancellation is not null))
            await ScanAsync(usbTriggered: true);
    }

    private void RenderPresence()
    {
        if (!_loaded || App.IsLocked) { StopPresence(); return; }
        if (_presenceMonitor is not { } monitor) return;
        var snapshot = monitor.Snapshot;
        if (snapshot.State != UsbPresenceState.Watching)
        {
            StopPresence();
            PresenceText.Text = "USB monitoring stopped unexpectedly. Current presence is unknown; no successful repair is claimed. Explicitly start again if needed.";
            return;
        }
        PresenceText.Text = $"Watching: {snapshot.PresentNodes} present Apple-vendor USB node(s). Initial enumeration is the baseline, not a reconnect.\n" +
            (snapshot.RecentChanges.Count == 0 ? "No node arrivals/removals observed since enumeration completed." :
                string.Join("\n", snapshot.RecentChanges.Select(change => $"{change.ObservedAt.LocalDateTime:HH:mm:ss}: node {(change.Added ? "arrived" : "removed")}; {change.PresentNodes} present."))) +
            "\nCounts are not phone count or proof of cable/hardware health. No identifiers or history are copied into the connection report.";
    }

    private void StopPresence()
    {
        _presenceEpoch++;
        _presenceCancellation?.Cancel();
        _presenceCancellation = null;
        if (_presenceTimer is { } timer) { timer.Stop(); timer.Tick -= PresenceTimer_Tick; _presenceTimer = null; }
        _presenceMonitor?.Dispose();
        _presenceMonitor = null;
        _presenceRefresh = null;
        if (_usbRefreshInProgress) _scanCancellation?.Cancel();
        PresenceButton.Content = "Start live USB monitor";
        PresenceText.Text = "USB monitor stopped; in-memory history cleared. Start explicitly to observe new changes.";
    }

    private async void CrashImportButton_Click(object sender, RoutedEventArgs args)
    {
        if (!_loaded || App.IsLocked || _crashCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _crashCancellation = cancellation;
        CrashImportButton.IsEnabled = CrashCopyButton.IsEnabled = false;
        _crashAnalysis = null;
        CrashAnalysisText.Text = string.Empty;
        try
        {
            var picker = new FileOpenPicker(App.MainWindow.AppWindow.Id);
            picker.FileTypeFilter.Add(".ips");
            picker.FileTypeFilter.Add(".json");
            var file = await picker.PickSingleFileAsync();
            if (file is null || cancellation.IsCancellationRequested || !_loaded || App.IsLocked) return;
            cancellation.CancelAfter(TimeSpan.FromSeconds(20));
            CrashAnalysisText.Text = "Analyzing locally…";
            await using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > IphoneCrashReportAnalyzer.MaximumBytes)
                throw new InvalidDataException("Report exceeds the 4 MiB analysis limit.");
            var analysis = await Task.Run(() => IphoneCrashReportAnalyzer.AnalyzeAsync(stream, cancellation.Token), cancellation.Token);
            if (!_loaded || cancellation.IsCancellationRequested || App.IsLocked) return;
            _crashAnalysis = analysis;
            CrashAnalysisText.Text = analysis.ToRedactedText();
            CrashCopyButton.IsEnabled = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (_loaded && !App.IsLocked)
                CrashAnalysisText.Text = "Analysis not completed. Choose a complete type-309 JSON .ips report up to 4 MiB. Malformed, ambiguous, oversized and other report formats are not supported. No repair was attempted and the original was not changed.";
        }
        finally
        {
            _crashCancellation = null;
            if (_loaded) CrashImportButton.IsEnabled = true;
        }
    }

    private void CrashCopyButton_Click(object sender, RoutedEventArgs args)
    {
        if (!_loaded || App.IsLocked || _crashAnalysis is null) return;
        try
        {
            var data = new DataPackage();
            data.SetText(_crashAnalysis.ToRedactedText());
            Clipboard.SetContent(data);
            ShowStatus("Redacted analysis copied", "Only the analysis shown here was copied, not the source report. Review it before sharing.", InfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { ShowStatus("Clipboard unavailable", "The analysis remains on this page.", InfoBarSeverity.Warning); }
    }

    private async Task ScanAsync(bool usbTriggered = false)
    {
        if (!_loaded || App.IsLocked || _scanCancellation is not null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _scanCancellation = cancellation;
        _usbRefreshInProgress = usbTriggered;
        ScanButton.IsEnabled = CopyButton.IsEnabled = ExperimentalButton.IsEnabled = false;
        PhoneInfoButton.IsEnabled = false;
        RecoveryButton.IsEnabled = false;
        PhoneInfoText.Text = string.Empty;
        ScanProgress.Visibility = Visibility.Visible;
        ScanProgress.IsActive = true;
        StatusBar.IsOpen = false;
        _report = null;
        _comparison = null;
        ComparisonJsonButton.IsEnabled = false;
        ComparisonText.Text = "Scan running. Previous completed aggregate observations remain in page memory; a failed scan will not count as improvement.";
        ClearCompanionHealthForPrivacy();
        Findings.Clear();
        try
        {
            var snapshot = await WindowsConnectionDiagnosticProbe.CaptureAsync(cancellation.Token);
            try { snapshot = snapshot with { HasPackageIdentity = LocalAppStorage.HasPackageIdentity }; }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { snapshot = snapshot with { UnavailableProbes = [.. snapshot.UnavailableProbes, "app-identity-unavailable"] }; }
            try
            {
                var server = await AppServices.Current.GetPairingServerAsync(cancellation.Token);
                var trusted = (await AppServices.Current.ListDevicesAsync(cancellation.Token))
                    .Where(device => device.TrustState == DeviceTrustState.Trusted).ToArray();
                snapshot = snapshot with
                {
                    TrustedCompanions = trusted.Length,
                    ConnectedCompanions = trusted.Count(device => server.IsConnected(device.Id)),
                };
                if (_loaded && !App.IsLocked && !cancellation.IsCancellationRequested)
                {
                    var healthSummaries = trusted.Select(device => server.TryGetSessionId(device.Id, out var sessionId)
                        ? (Device: device.Id, Report: AppServices.Current.GetCompanionHealth(device.Id, sessionId))
                        : (Device: device.Id, Report: (ReceivedCompanionHealth?)null))
                        .Where(item => item.Report is not null)
                        .Select(item => $"Companion identity {item.Device.Value.ToString("D")[..8]} (short label):\n{item.Report!.ToDisplayText()}");
                    var text = string.Join("\n\n", healthSummaries);
                    CompanionHealthText.Text = text.Length > 0 ? text : "No accepted health snapshot from an active trusted session in the last five minutes. Send one explicitly from the iPhone companion, then run checks again.";
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                snapshot = snapshot with { UnavailableProbes = [.. snapshot.UnavailableProbes, "companion-query-unavailable"] };
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_loaded || App.IsLocked) return;
            _report = ConnectionDiagnosticEvaluator.Evaluate(snapshot);
            _comparison = _connectionHistory.Observe(snapshot);
            ComparisonText.Text = _comparison.ToShareableText();
            ComparisonJsonButton.IsEnabled = true;
            foreach (var finding in _report.Findings) Findings.Add(finding);
            var trigger = usbTriggered ? "USB node change refresh" : "Snapshot";
            SnapshotText.Text = $"{trigger}: {snapshot.CapturedAt.LocalDateTime:g}. {snapshot.UnavailableProbes.Count} unavailable probe(s). No phone changes made.";
            if (AppServices.Current.LastDiscoveryRecovery is { } recovery)
                RecoveryText.Text = $"Last app discovery recovery this run: {recovery.Outcome}. {recovery.Summary} This is separate from the copied read-only snapshot.";
            CopyButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            if (_loaded && !App.IsLocked)
            {
                ComparisonText.Text = "Scan cancelled or timed out. No new comparison was recorded; prior observations do not establish current state.";
                ShowStatus("Scan stopped", "The scan was cancelled or timed out. Run it again after checking the connection.", InfoBarSeverity.Warning);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (_loaded && !App.IsLocked)
            {
                ComparisonText.Text = "Scan unavailable. No new comparison was recorded; prior observations do not establish current state.";
                ShowStatus("Scan unavailable", "Windows could not complete this scan. No repair was attempted.", InfoBarSeverity.Error);
            }
        }
        finally
        {
            _usbRefreshInProgress = false;
            _scanCancellation = null;
            if (_loaded)
            {
                ScanButton.IsEnabled = true;
                ExperimentalButton.IsEnabled = true;
                PhoneInfoButton.IsEnabled = true;
                RecoveryButton.IsEnabled = true;
                ScanProgress.IsActive = false;
                ScanProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    private async void ExperimentalButton_Click(object sender, RoutedEventArgs args)
    {
        if (_scanCancellation is not null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        _scanCancellation = cancellation;
        ScanButton.IsEnabled = CopyButton.IsEnabled = ExperimentalButton.IsEnabled = false;
        PhoneInfoButton.IsEnabled = false;
        RecoveryButton.IsEnabled = false;
        ScanProgress.IsActive = true;
        ScanProgress.Visibility = Visibility.Visible;
        DiagnosticFinding finding;
        var guide = new Uri("https://doronz88.github.io/pymobiledevice3/guides/network-stacks/");
        try
        {
            var inventory = await ExperimentalUsbMuxProbe.ProbeAsync(cancellation.Token);
            finding = new("experimental-usbmux", "Experimental Apple transport inventory",
                inventory.UsbConnections + inventory.NetworkConnections > 0 ? DiagnosticOutcome.Passed : DiagnosticOutcome.Attention,
                $"The local Apple transport replied: {inventory.UsbConnections} USB, {inventory.NetworkConnections} network, {inventory.OtherConnections} other connection(s). This does not verify device ownership, USB Trust, or hardware health.",
                "This reverse-engineered protocol check only lists connections. If zero are listed, unlock and reconnect the phone with a data cable and inspect Apple Devices. No phone commands, pairing records, or changes were requested.", guide);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            finding = new("experimental-usbmux", "Experimental Apple transport inventory", DiagnosticOutcome.Unknown,
                "The experimental probe was cancelled or timed out.", "Retry the explicit probe if needed. No repair or phone change was attempted.", guide);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            finding = new("experimental-usbmux", "Experimental Apple transport inventory", DiagnosticOutcome.Unknown,
                "The local service was unavailable or its response was not supported by this experimental probe. This is not evidence of a phone fault.",
                "Check Apple Devices first. This implementation does not install software, start services, bypass trust, or fall back to undocumented write commands.", guide);
        }
        finally
        {
            _scanCancellation = null;
            if (_loaded)
            {
                ScanButton.IsEnabled = ExperimentalButton.IsEnabled = true;
                PhoneInfoButton.IsEnabled = true;
                RecoveryButton.IsEnabled = true;
                ScanProgress.IsActive = false;
                ScanProgress.Visibility = Visibility.Collapsed;
            }
        }
        if (!_loaded || cancellation.IsCancellationRequested) return;
        _report ??= ConnectionDiagnosticEvaluator.Evaluate(new ConnectionDiagnosticSnapshot(
            DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, ["initial-scan-unavailable"]));
        _report = _report with { Findings = [.. _report.Findings.Where(item => item.Id != finding.Id), finding] };
        Findings.Clear();
        foreach (var item in _report.Findings) Findings.Add(item);
        CopyButton.IsEnabled = true;
    }

    private async void PhoneInfoButton_Click(object sender, RoutedEventArgs args)
    {
        if (_scanCancellation is not null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _scanCancellation = cancellation;
        ScanButton.IsEnabled = CopyButton.IsEnabled = ExperimentalButton.IsEnabled = PhoneInfoButton.IsEnabled = false;
        RecoveryButton.IsEnabled = false;
        ScanProgress.IsActive = true;
        ScanProgress.Visibility = Visibility.Visible;
        PhoneInfoText.Text = "Reading allowlisted information from one USB-connected device…";
        try
        {
            var information = await ExperimentalIphoneInformationProbe.ProbeAsync(cancellation.Token);
            if (!_loaded || cancellation.IsCancellationRequested) return;
            static string Flag(bool? value) => value is null ? "unknown" : value.Value ? "yes" : "no";
            var batteryText = information.BatteryPercentage is { } level ? $"{level}%" : "unknown";
            PhoneInfoText.Text = $"Read at {DateTimeOffset.Now:t}: {information.DeviceClass ?? "unknown class"} · {information.ProductType ?? "unknown hardware identifier"} · iOS {information.IosVersion ?? "unknown"} · build {information.BuildVersion ?? "unknown"}. " +
                $"Battery: {batteryText} · charging: {Flag(information.IsCharging)} · external power: {Flag(information.ExternalPowerConnected)}. " +
                $"{information.UnavailableFields} unavailable field(s). No pairing credentials were read and no phone changes were made. These values are not included in the copied report.\n" +
                string.Join("\n", information.Fields.Where(field => field.Status != IphoneInformationFieldStatus.Available)
                    .Select(field => $"{field.Key}: {field.Status}" + (field.DeviceErrorCode is null ? "." : $" ({field.DeviceErrorCode})."))) +
                "\nAn unavailable value is not a battery-health or hardware-failure diagnosis.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (_loaded) PhoneInfoText.Text = "Information could not be read. Keep exactly one owned iPhone connected by USB, unlock it, and check any Trust prompt yourself. Protected or unsupported values remain unknown; this check does not bypass security.";
        }
        finally
        {
            _scanCancellation = null;
            if (_loaded)
            {
                ScanButton.IsEnabled = ExperimentalButton.IsEnabled = PhoneInfoButton.IsEnabled = true;
                RecoveryButton.IsEnabled = true;
                CopyButton.IsEnabled = _report is not null;
                ScanProgress.IsActive = false;
                ScanProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    private async void RecoveryButton_Click(object sender, RoutedEventArgs args)
    {
        if (!_loaded || _scanCancellation is not null || App.IsLocked) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        _scanCancellation = cancellation;
        ScanButton.IsEnabled = CopyButton.IsEnabled = ExperimentalButton.IsEnabled = PhoneInfoButton.IsEnabled = RecoveryButton.IsEnabled = false;
        ScanProgress.IsActive = true;
        ScanProgress.Visibility = Visibility.Visible;
        RecoveryText.Text = "Checking app discovery prerequisites before attempting recovery…";
        try
        {
            var result = await AppServices.Current.RecoverDiscoveryAsync(cancellation.Token);
            if (!_loaded || cancellation.IsCancellationRequested || App.IsLocked) return;
            RecoveryText.Text = $"{result.Outcome}: {result.Summary} " +
                $"Before: {result.Before.TrustedCompanions} trusted companion(s), {result.Before.EligibleAddresses} local address(es), registered={result.Before.AdvertisementRegistered}. " +
                (result.After is { } after ? $"After: {after.TrustedCompanions} trusted companion(s), {after.EligibleAddresses} local address(es), registered={after.AdvertisementRegistered}. " : "No after-state recorded. ") +
                $"Recovery attempted={result.ActionAttempted}. This separate action is not included in the read-only scan report.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (_loaded) RecoveryText.Text = "Discovery recovery stopped, timed out, or could not be verified. No successful repair is claimed. Existing trust, phone data and drivers were not reset.";
        }
        finally
        {
            _scanCancellation = null;
            if (_loaded)
            {
                ScanButton.IsEnabled = ExperimentalButton.IsEnabled = PhoneInfoButton.IsEnabled = RecoveryButton.IsEnabled = true;
                CopyButton.IsEnabled = _report is not null;
                ScanProgress.IsActive = false;
                ScanProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs args)
    {
        if (!_loaded || App.IsLocked || _report is null) return;
        try
        {
            var data = new DataPackage();
            data.SetText(_report.ToShareableText() + (_comparison is null ? string.Empty : "\n\n" + _comparison.ToShareableText()));
            Clipboard.SetContent(data);
            ShowStatus("Report copied", "The clipboard report contains findings, counts, and next steps—not device identifiers or phone content.", InfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowStatus("Clipboard unavailable", "Windows did not accept the report. Your findings remain on this page.", InfoBarSeverity.Warning);
        }
    }

    private void ComparisonJsonButton_Click(object sender, RoutedEventArgs args)
    {
        if (!_loaded || App.IsLocked || _comparison is null) return;
        try
        {
            var data = new DataPackage();
            data.SetText(_comparison.ToShareableJson());
            Clipboard.SetContent(data);
            ShowStatus("Comparison JSON copied", "Only fixed rule text, outcomes, counts and timestamps were copied. Review before sharing. Phone content, identifiers and raw probe errors are excluded.", InfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { ShowStatus("Clipboard unavailable", "The comparison remains on this page.", InfoBarSeverity.Warning); }
    }

    private async void AppleDevicesButton_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var package = await Task.Run(() => new PackageManager().FindPackagesForUser(string.Empty)
                .FirstOrDefault(candidate => candidate.Id.Name == "AppleInc.AppleDevices"));
            if (package is not null)
            {
                var entries = await package.GetAppListEntriesAsync();
                if (entries.Count > 0 && await entries[0].LaunchAsync()) return;
            }
            if (_loaded) ShowStatus("Apple Devices could not be opened", "Install or open Apple's Apple Devices app from Windows Start. No installation or recovery action has been run.", InfoBarSeverity.Warning);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (_loaded) ShowStatus("Apple Devices could not be opened", "Open it from Windows Start. This action does not automatically start an update or restore.", InfoBarSeverity.Warning);
        }
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }
}
