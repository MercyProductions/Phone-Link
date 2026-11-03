using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhoneLink.Application.Features.Diagnostics;
using PhoneLink.DeviceCommunication.Diagnostics;
using PhoneLink.Domain.Devices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Management.Deployment;

namespace PhoneLink.Windows.Pages;

public sealed partial class DiagnosticsPage : Page
{
    private CancellationTokenSource? _scanCancellation;
    private ConnectionDiagnosticReport? _report;
    private bool _loaded;
    public ObservableCollection<DiagnosticFinding> Findings { get; } = [];

    public DiagnosticsPage() { InitializeComponent(); }

    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        _loaded = true;
        await ScanAsync();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs args)
    {
        _loaded = false;
        _scanCancellation?.Cancel();
        PhoneInfoText.Text = string.Empty;
        RecoveryText.Text = string.Empty;
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs args) => await ScanAsync();

    private async Task ScanAsync()
    {
        if (_scanCancellation is not null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _scanCancellation = cancellation;
        ScanButton.IsEnabled = CopyButton.IsEnabled = ExperimentalButton.IsEnabled = false;
        PhoneInfoButton.IsEnabled = false;
        RecoveryButton.IsEnabled = false;
        PhoneInfoText.Text = string.Empty;
        ScanProgress.Visibility = Visibility.Visible;
        ScanProgress.IsActive = true;
        StatusBar.IsOpen = false;
        _report = null;
        Findings.Clear();
        try
        {
            var snapshot = await WindowsConnectionDiagnosticProbe.CaptureAsync(cancellation.Token);
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
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                snapshot = snapshot with { UnavailableProbes = [.. snapshot.UnavailableProbes, "companion-query-unavailable"] };
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_loaded) return;
            _report = ConnectionDiagnosticEvaluator.Evaluate(snapshot);
            foreach (var finding in _report.Findings) Findings.Add(finding);
            SnapshotText.Text = $"Snapshot: {snapshot.CapturedAt.LocalDateTime:g}. {snapshot.UnavailableProbes.Count} unavailable probe(s). No phone changes made.";
            if (AppServices.Current.LastDiscoveryRecovery is { } recovery)
                RecoveryText.Text = $"Last app discovery recovery this run: {recovery.Outcome}. {recovery.Summary} This is separate from the copied read-only snapshot.";
            CopyButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            if (_loaded) ShowStatus("Scan stopped", "The scan was cancelled or timed out. Run it again after checking the connection.", InfoBarSeverity.Warning);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (_loaded) ShowStatus("Scan unavailable", "Windows could not complete this scan. No repair was attempted.", InfoBarSeverity.Error);
        }
        finally
        {
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
        if (_report is null) return;
        try
        {
            var data = new DataPackage();
            data.SetText(_report.ToShareableText());
            Clipboard.SetContent(data);
            ShowStatus("Report copied", "The clipboard report contains findings, counts, and next steps—not device identifiers or phone content.", InfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowStatus("Clipboard unavailable", "Windows did not accept the report. Your findings remain on this page.", InfoBarSeverity.Warning);
        }
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
