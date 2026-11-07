using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhoneLink.Application.Features.BinaryInspection;
using PhoneLink.DeviceCommunication.Diagnostics;
using PhoneLink.DeviceCommunication.Tunnels;
using Windows.Storage;

namespace PhoneLink.Windows.Pages;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WinUI owns the page; Unloaded cancels operations and disposes the captured lifetime after cleanup.")]
public sealed partial class AdvancedToolsPage : Page
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new() { WriteIndented = true };
    private CancellationTokenSource _pageLifetime = new();
    private Task? _operation;
    private LoopbackDeviceTunnel? _tunnel;
    private CancellationTokenSource? _sshCancellation;
    private CancellationTokenSource? _operationCancellation;
    private ushort _forwardedPort;
    private MachOInspection? _inspection;
    private int _runtimeEpoch;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _loaded;
    private bool _busy;
    private bool _liveRuntimeEnabled;
    public ObservableCollection<OffsetEntryRow> Rows { get; } = [];
    public ObservableCollection<RuntimeOffsetRow> RuntimeRows { get; } = [];

    public AdvancedToolsPage()
    {
        InitializeComponent();
        _timer.Tick += Timer_Tick;
    }

    private void Page_Loaded(object sender, RoutedEventArgs args)
    {
        if (_pageLifetime.IsCancellationRequested) _pageLifetime = new CancellationTokenSource();
        _loaded = true;
        _timer.Start();
        UpdateButtons();
    }

    private async void Page_Unloaded(object sender, RoutedEventArgs args)
    {
        _loaded = false;
        _sshCancellation?.Cancel();
        ClearSshClientCommand();
        SshProbeText.Text = "No current forwarded SSH observation.";
        ClearRuntimeForPrivacy();
        SetLiveRuntime(false);
        _timer.Stop();
        var lifetime = _pageLifetime;
        var operation = _operation;
        var tunnel = Interlocked.Exchange(ref _tunnel, null);
        await lifetime.CancelAsync();
        try
        {
            if (tunnel is not null) await tunnel.DisposeAsync();
            if (operation is not null) await operation;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { /* No payload or file path logging. */ }
        finally { lifetime.Dispose(); }
    }

    private Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (!_loaded || _busy || App.IsLocked) return Task.CompletedTask;
        return _operation = ExecuteAsync(action, _pageLifetime.Token);
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _operationCancellation = operationCancellation;
        cancellationToken = operationCancellation.Token;
        _busy = true;
        UpdateButtons();
        StatusBar.IsOpen = false;
        BusyRing.IsActive = true;
        BusyRing.Visibility = Visibility.Visible;
        try { await action(cancellationToken); }
        catch (OperationCanceledException)
        {
            if (_loaded && !cancellationToken.IsCancellationRequested) ShowStatus("Operation timed out", InfoBarSeverity.Warning);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (_loaded && !cancellationToken.IsCancellationRequested)
                ShowStatus($"Operation not completed: {exception.Message}", InfoBarSeverity.Warning);
        }
        finally
        {
            _operationCancellation = null;
            _busy = false;
            if (_loaded)
            {
                BusyRing.IsActive = false;
                BusyRing.Visibility = Visibility.Collapsed;
                UpdateButtons();
            }
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs args) => await RunAsync(async token =>
    {
        var port = ReadRemotePort();
        using var setup = CancellationTokenSource.CreateLinkedTokenSource(token);
        setup.CancelAfter(TimeSpan.FromSeconds(10));
        var connector = await PinnedUsbDeviceConnector.SelectSingleAsync(setup.Token);
        token.ThrowIfCancellationRequested();
        if (!_loaded || App.IsLocked) return;
        _tunnel = LoopbackDeviceTunnel.Start(ct => connector.OpenChannelAsync(port, ct), cancellationToken: _pageLifetime.Token);
        _forwardedPort = port;
        SshProbeText.Text = "This forwarder's target service has not been checked. Identification will not establish host-key trust or login.";
        TunnelText.Text = $"127.0.0.1:{_tunnel.LocalPort} → selected USB device port {port}. Target service and authentication are not verified.";
    });

    private async void StopButton_Click(object sender, RoutedEventArgs args)
    {
        try { await StopTunnelAsync(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { if (_loaded) ShowStatus($"Stopping failed: {exception.Message}", InfoBarSeverity.Warning); }
    }

    private async Task StopTunnelAsync()
    {
        _sshCancellation?.Cancel();
        ClearSshClientCommand();
        SshProbeText.Text = "No current forwarded SSH observation. Stopping invalidates earlier results.";
        var tunnel = Interlocked.Exchange(ref _tunnel, null);
        if (tunnel is not null) await tunnel.DisposeAsync();
        if (_loaded) { TunnelText.Text = "Stopped. No ports are listening."; UpdateButtons(); }
    }

    private async void VerifyButton_Click(object sender, RoutedEventArgs args) => await RunAsync(async token =>
    {
        var result = await UsbTunnelVerifier.VerifyAsync(token);
        if (!token.IsCancellationRequested && _loaded)
            ShowStatus(result.Verified
                ? $"USB round-trip verified through a temporary loopback forwarder: {result.Statistics.BytesToDevice} bytes sent, {result.Statistics.BytesFromDevice} received; temporary connections closed. No phone change was made."
                : "The transport completed, but a supported information value was not returned. No repair was attempted.",
                result.Verified ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    });

    private async void SshButton_Click(object sender, RoutedEventArgs args) => await RunAsync(async token =>
    {
        var tunnel = _tunnel;
        if (tunnel is null || !tunnel.IsRunning) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _sshCancellation = cancellation;
        ClearSshClientCommand();
        SshProbeText.Text = "Checking identification through this active forwarder only…";
        try
        {
            var result = await ForwardedSshAvailabilityProbe.ProbeAsync(tunnel, cancellation.Token);
            if (!cancellation.IsCancellationRequested && _loaded && !App.IsLocked &&
                ReferenceEquals(_tunnel, tunnel) && tunnel.IsRunning)
            {
                SshProbeText.Text = $"Forwarded USB port {_forwardedPort}: {result.Status}";
                if (result.ServerIdentified)
                {
                    SshClientText.Text = "Optional: in your own terminal, replace YOUR_USER and VERIFIED_ALIAS. " +
                        "Use an alias that already has this server's independently verified key in known_hosts. " +
                        "Phone Link does not enroll or verify the key.\n" + FormattableString.Invariant(
                            $"ssh -F none -o StrictHostKeyChecking=yes -o UpdateHostKeys=no -o ForwardAgent=no -o HostKeyAlias=VERIFIED_ALIAS -p {tunnel.LocalPort} -l YOUR_USER 127.0.0.1");
                    SshClientText.Visibility = Visibility.Visible;
                }
            }
        }
        finally { _sshCancellation = null; }
    });

    private async void DirectSshButton_Click(object sender, RoutedEventArgs args) => await RunAsync(async token =>
    {
        if (_tunnel is not null) return;
        var port = ReadRemotePort();
        ClearSshClientCommand();
        SshProbeText.Text = $"Checking USB port {port}; no listener will be opened…";
        var result = await ExperimentalSshAvailabilityProbe.ProbeAsync(port, token);
        if (!_loaded || App.IsLocked || token.IsCancellationRequested) return;
        var code = result.TransportResponseCode is { } response ? $" Transport response: {response}." : string.Empty;
        SshProbeText.Text = $"USB port {port}: {result.Status}{code}";
    });

    private ushort ReadRemotePort()
    {
        var value = RemotePortBox.Value;
        if (double.IsNaN(value) || value != Math.Truncate(value) || value is < 1 or > 65535)
            throw new InvalidOperationException("Choose a whole-number device port between 1 and 65535.");
        return (ushort)value;
    }

    private void ClearSshClientCommand()
    {
        SshClientText.Text = string.Empty;
        SshClientText.Visibility = Visibility.Collapsed;
    }

    private async void InspectButton_Click(object sender, RoutedEventArgs args) => await RunAsync(async token =>
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.MainWindow.AppWindow.Id);
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        if (file is null || token.IsCancellationRequested || !_loaded || App.IsLocked) return;
        _inspection = null;
        ImageSelector.Items.Clear();
        Rows.Clear();
        ImageSummary.Text = "Inspecting selected image…";
        EntrySummary.Text = "No entries available until inspection succeeds.";
        await using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var result = await MachOInspector.InspectAsync(stream, token);
        if (token.IsCancellationRequested || !_loaded || App.IsLocked) return;
        _inspection = result;
        ImageSummary.Text = $"{result.ByteLength:N0} bytes · SHA-256 {result.Sha256}\nStatic analysis only. Runtime addresses are unavailable; no ASLR slide is assumed.";
        foreach (var image in result.Images) ImageSelector.Items.Add($"{image.Architecture} · {image.Uuid ?? "no UUID"}");
        if (result.Images.Count > 0) ImageSelector.SelectedIndex = 0;
    });

    private async void DeveloperModeButton_Click(object sender, RoutedEventArgs args) => await RunAsync(async token =>
    {
        if (App.IsLocked) return;
        var result = await ExperimentalIphoneInformationProbe.ProbeDeveloperModeAsync(token);
        if (!_loaded || token.IsCancellationRequested || App.IsLocked) return;
        var flag = result.ReportedEnabled switch { true => "enabled", false => "disabled", _ => "unknown" };
        ShowStatus($"Reported Developer Mode: {flag}. Field status: {result.Field.Status}" +
            (result.Field.DeviceErrorCode is { } code ? $" ({code})" : "") +
            $". Read at {result.CapturedAt.LocalDateTime:g}. This no-session observation does not authenticate phone identity or prove debugger/SSH access. No setting, service, trust or firmware was changed.",
            result.ReportedEnabled is null ? InfoBarSeverity.Warning : InfoBarSeverity.Informational);
    });

    private void ImageSelector_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        ClearRuntimeForPrivacy();
        Rows.Clear();
        var index = ImageSelector.SelectedIndex;
        if (_inspection is null || index < 0 || index >= _inspection.Images.Count) return;
        var image = _inspection.Images[index];
        foreach (var entry in image.Entries.Take(500)) Rows.Add(new OffsetEntryRow(entry));
        var targets = image.DeclaredBuildTargets.Count == 0 ? "No LC_BUILD_VERSION declaration." :
            "Declared targets: " + string.Join("; ", image.DeclaredBuildTargets.Select(
                target => $"{target.Platform}, minimum {target.MinimumOs}, SDK {target.Sdk ?? "not declared"}")) + ".";
        var signature = image.DeclaredSignature is { ByteLength: > 0 } declared ?
            $"Signature range declared at {declared.FileOffset} ({declared.ByteLength:N0} bytes), not verified." :
            "No nonempty signature range declared.";
        EntrySummary.Text = $"Showing {Rows.Count:N0} of {image.Entries.Count:N0} parsed entries; declared symbol records: {image.DeclaredSymbolCount:N0}. " +
            $"Output partial: {image.EntriesTruncated}. Encrypted content: {image.EncryptedContent}. " + string.Join(" ", image.Warnings) +
            $"\n{targets} {signature} Load-command directory in JSON: {image.LoadCommands.Count:N0} of {image.DeclaredLoadCommandCount:N0}. " +
            "Names may be shortened in the table; JSON retains parsed names. Constants and missing mappings are not file/runtime addresses.";
    }

    public void ClearRuntimeForPrivacy()
    {
        _runtimeEpoch++;
        RuntimeRows.Clear();
        if (RuntimeSummary is not null)
            RuntimeSummary.Text = "No runtime snapshot displayed. Static JSON export never includes imported runtime addresses.";
    }

    public async void SetRuntimePrivacyLocked(bool locked)
    {
        if (locked)
        {
            _operationCancellation?.Cancel();
            RuntimeSymbolFilter.Text = string.Empty;
            ClearRuntimeForPrivacy();
            SetLiveRuntime(false);
            try { await StopTunnelAsync(); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { SshProbeText.Text = "Forwarder stopping could not be verified. Earlier SSH observations are invalid."; }
        }
        UpdateButtons();
    }

    private void RuntimeSymbolFilter_TextChanged(object sender, TextChangedEventArgs args) => ClearRuntimeForPrivacy();

    private async void RuntimeImportButton_Click(object sender, RoutedEventArgs args) => await RunAsync(async token =>
    {
        var inspection = _inspection;
        var index = ImageSelector.SelectedIndex;
        if (inspection is null || index < 0 || index >= inspection.Images.Count || App.IsLocked) return;
        ClearRuntimeForPrivacy();
        var epoch = _runtimeEpoch;
        var selectedImage = inspection.Images[index];
        var symbol = RuntimeSymbolFilter.Text.Length == 0 ? null : RuntimeSymbolFilter.Text;
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.MainWindow.AppWindow.Id);
        picker.FileTypeFilter.Add(".json");
        var file = await picker.PickSingleFileAsync();
        if (file is null || token.IsCancellationRequested || !_loaded || App.IsLocked || epoch != _runtimeEpoch) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > CompanionRuntimeMapper.MaximumSnapshotBytes)
            throw new InvalidDataException("Runtime snapshot exceeds 8 KiB.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, deadline.Token);
        var mapping = await Task.Run(() => CompanionRuntimeMapper.Map(inspection with { Images = [selectedImage] }, bytes, symbol), deadline.Token);
        if (deadline.IsCancellationRequested || !_loaded || App.IsLocked || epoch != _runtimeEpoch) return;
        RuntimeSummary.Text = $"Reported capture: {mapping.ReportedCaptureTime:O}\nMatched {mapping.Architecture} · UUID {mapping.ImageUuid}. " +
            (mapping.ExactSymbolName is null ? "No symbol filter. " : "Exact symbol filter applied (case-sensitive). ") +
            $"Showing up to 500 of {mapping.TextEntries.Count} mapped text entries. Partial output: {mapping.EntriesTruncated}.\n" +
            "Snapshot origin authenticated: false. Live process verified: false.\n" + mapping.Warning;
        foreach (var entry in mapping.TextEntries.Take(500)) RuntimeRows.Add(new(entry));
    });

    private async void ExportButton_Click(object sender, RoutedEventArgs args) => await RunAsync(async token =>
    {
        var report = _inspection;
        if (report is null) return;
        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(App.MainWindow.AppWindow.Id)
            { SuggestedFileName = "PhoneLink-offsets", DefaultFileExtension = ".json", ShowOverwritePrompt = true };
        picker.FileTypeChoices.Add("JSON offset report", [".json"]);
        var selected = await picker.PickSaveFileAsync();
        if (selected is null || token.IsCancellationRequested || !_loaded || App.IsLocked) return;
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(selected.Path)!);
        token.ThrowIfCancellationRequested();
        // The desktop picker returns a path, not a StorageFile. Open/create without
        // truncating existing content, then retain transactional replacement below.
        var file = await folder.CreateFileAsync(Path.GetFileName(selected.Path), CreationCollisionOption.OpenIfExists);
        token.ThrowIfCancellationRequested();
        if (!_loaded || App.IsLocked) return;
        using var transaction = await file.OpenTransactedWriteAsync();
        transaction.Stream.Size = 0;
        using (var stream = transaction.Stream.AsStreamForWrite())
        {
            await JsonSerializer.SerializeAsync(stream, report, ReportJsonOptions, token);
            await stream.FlushAsync(token);
            token.ThrowIfCancellationRequested();
            if (!_loaded || App.IsLocked) return;
            await transaction.CommitAsync();
        }
        if (_loaded) ShowStatus("Static offset report saved. No image or phone data was modified.", InfoBarSeverity.Success);
    });

    private async void Timer_Tick(object? sender, object args)
    {
        if (_liveRuntimeEnabled)
        {
            if (App.IsLocked) SetLiveRuntime(false);
            else if (AppServices.Current.GetRuntimeSample() is { } sample)
                LiveRuntimeText.Text = $"Authenticated companion sample at {sample.CapturedAt.LocalDateTime:HH:mm:ss.fff}\n" +
                    $"Loaded image base {sample.LoadedAddressHex} · marker {sample.MarkerAddressHex} · ASLR slide {sample.Slide}\n" +
                    $"{sample.Architecture} · image {sample.ImageUuid:D}\n" +
                    "Reported by the companion, not independently read by Windows. Addresses apply only to this process lifetime.";
            else LiveRuntimeText.Text = "Waiting for a fresh sample. Earlier addresses have expired or the session ended.";
        }
        var tunnel = _tunnel;
        if (tunnel is null) return;
        if (App.IsLocked || !tunnel.IsRunning)
        {
            try { await StopTunnelAsync(); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
            return;
        }
        var stats = tunnel.CaptureStatistics();
        TunnelText.Text = $"127.0.0.1:{tunnel.LocalPort} → USB port {_forwardedPort} · {stats.ActiveConnections} active · " +
            $"{stats.BytesToDevice:N0} bytes sent / {stats.BytesFromDevice:N0} received · {stats.FailedConnections} failed · {stats.RejectedAtCapacity} declined at capacity · {stats.LastFailureCategory ?? "no failure recorded"}. " +
            "Forwarding active; remote service and SSH authentication are not verified.";
    }

    private void LiveRuntimeButton_Click(object sender, RoutedEventArgs args)
    {
        if (_loaded && !App.IsLocked) SetLiveRuntime(!_liveRuntimeEnabled);
    }

    private void SetLiveRuntime(bool enabled)
    {
        _liveRuntimeEnabled = enabled;
        AppServices.Current.SetRuntimeFeedArmed(enabled);
        LiveRuntimeButton.Content = enabled ? "Stop live address view" : "Enable live address view";
        LiveRuntimeText.Text = enabled ? "Waiting for the iPhone to start its feed…" : "Live view is off.";
    }

    private void UpdateButtons()
    {
        StartButton.IsEnabled = !_busy && !App.IsLocked && _tunnel is null;
        StopButton.IsEnabled = _tunnel is not null;
        RemotePortBox.IsEnabled = !_busy && _tunnel is null;
        VerifyButton.IsEnabled = InspectButton.IsEnabled = DeveloperModeButton.IsEnabled = !_busy && !App.IsLocked;
        SshButton.IsEnabled = !_busy && !App.IsLocked && _tunnel?.IsRunning == true;
        DirectSshButton.IsEnabled = !_busy && !App.IsLocked && _tunnel is null;
        ExportButton.IsEnabled = !_busy && _inspection is not null;
        RuntimeImportButton.IsEnabled = !_busy && !App.IsLocked && _inspection is not null;
        ImageSelector.IsEnabled = !_busy;
        RuntimeSymbolFilter.IsEnabled = !_busy;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    { StatusBar.Message = message; StatusBar.Severity = severity; StatusBar.IsOpen = true; }
}

public sealed class RuntimeOffsetRow(SnapshotMappedOffset entry)
{
    public string Name { get; } = new OffsetEntryRow(entry.StaticEntry).Name;
    public string Address { get; } = "Snapshot-derived only: " + entry.SnapshotDerivedAddress;
}

public sealed class OffsetEntryRow
{
    public OffsetEntryRow(MachOOffsetEntry entry)
    {
        var name = string.Concat(entry.Name.Take(200).Select(character => char.IsControl(character)
            ? "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture) : character.ToString()));
        Name = name + (entry.Name.Length > 200 ? "…" : "") + "  [" + entry.Source + "]";
        Mapping = $"{entry.VirtualAddress ?? "—"} · {entry.ImageRelativeOffset ?? "—"} · {entry.FileOffset ?? "—"}";
        Kind = entry.Kind + (entry.AbsoluteValue is null ? "" : " · constant " + entry.AbsoluteValue);
    }
    public string Name { get; }
    public string Mapping { get; }
    public string Kind { get; }
}
