using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhoneLink.Application.Features.BinaryInspection;
using PhoneLink.DeviceCommunication.Diagnostics;
using PhoneLink.DeviceCommunication.Tunnels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PhoneLink.Windows.Pages;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WinUI owns the page; Unloaded cancels operations and disposes the captured lifetime after cleanup.")]
public sealed partial class AdvancedToolsPage : Page
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new() { WriteIndented = true };
    private CancellationTokenSource _pageLifetime = new();
    private Task? _operation;
    private LoopbackDeviceTunnel? _tunnel;
    private MachOInspection? _inspection;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _loaded;
    private bool _busy;
    public ObservableCollection<OffsetEntryRow> Rows { get; } = [];

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
        if (!_loaded || _busy) return Task.CompletedTask;
        return _operation = ExecuteAsync(action, _pageLifetime.Token);
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
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
        var value = RemotePortBox.Value;
        if (double.IsNaN(value) || value != Math.Truncate(value) || value is < 1 or > 65535)
            throw new InvalidOperationException("Choose a whole-number device port between 1 and 65535.");
        using var setup = CancellationTokenSource.CreateLinkedTokenSource(token);
        setup.CancelAfter(TimeSpan.FromSeconds(10));
        var connector = await PinnedUsbDeviceConnector.SelectSingleAsync(setup.Token);
        token.ThrowIfCancellationRequested();
        var port = (ushort)value;
        _tunnel = LoopbackDeviceTunnel.Start(ct => connector.OpenChannelAsync(port, ct), cancellationToken: _pageLifetime.Token);
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
        var result = await ExperimentalSshAvailabilityProbe.ProbeAsync(token);
        if (!token.IsCancellationRequested && _loaded)
            ShowStatus(result.Status, result.ServerIdentified ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
    });

    private async void InspectButton_Click(object sender, RoutedEventArgs args) => await RunAsync(async token =>
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSingleFileAsync();
        if (file is null || token.IsCancellationRequested || App.IsLocked) return;
        _inspection = null;
        ImageSelector.Items.Clear();
        Rows.Clear();
        ImageSummary.Text = "Inspecting selected image…";
        EntrySummary.Text = "No entries available until inspection succeeds.";
        using var random = await file.OpenReadAsync();
        using var stream = random.AsStream();
        var result = await MachOInspector.InspectAsync(stream, token);
        if (token.IsCancellationRequested || !_loaded) return;
        _inspection = result;
        ImageSummary.Text = $"{result.ByteLength:N0} bytes · SHA-256 {result.Sha256}\nStatic analysis only. Runtime addresses are unavailable; no ASLR slide is assumed.";
        foreach (var image in result.Images) ImageSelector.Items.Add($"{image.Architecture} · {image.Uuid ?? "no UUID"}");
        if (result.Images.Count > 0) ImageSelector.SelectedIndex = 0;
    });

    private void ImageSelector_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        Rows.Clear();
        var index = ImageSelector.SelectedIndex;
        if (_inspection is null || index < 0 || index >= _inspection.Images.Count) return;
        var image = _inspection.Images[index];
        foreach (var entry in image.Entries.Take(500)) Rows.Add(new OffsetEntryRow(entry));
        EntrySummary.Text = $"Showing {Rows.Count:N0} of {image.Entries.Count:N0} parsed entries; declared symbol records: {image.DeclaredSymbolCount:N0}. " +
            $"Output partial: {image.EntriesTruncated}. Encrypted content: {image.EncryptedContent}. " + string.Join(" ", image.Warnings) +
            " Names may be shortened in the table; JSON retains parsed names. Constants and missing mappings are not file/runtime addresses.";
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs args) => await RunAsync(async token =>
    {
        var report = _inspection;
        if (report is null) return;
        var picker = new FileSavePicker { SuggestedFileName = "PhoneLink-offsets" };
        picker.FileTypeChoices.Add("JSON offset report", [".json"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSaveFileAsync();
        if (file is null || token.IsCancellationRequested || App.IsLocked) return;
        using var transaction = await file.OpenTransactedWriteAsync();
        transaction.Stream.Size = 0;
        using (var stream = transaction.Stream.AsStreamForWrite())
        {
            await JsonSerializer.SerializeAsync(stream, report, ReportJsonOptions, token);
            await stream.FlushAsync(token);
            token.ThrowIfCancellationRequested();
            await transaction.CommitAsync();
        }
        if (_loaded) ShowStatus("Static offset report saved. No image or phone data was modified.", InfoBarSeverity.Success);
    });

    private async void Timer_Tick(object? sender, object args)
    {
        var tunnel = _tunnel;
        if (tunnel is null) return;
        if (App.IsLocked || !tunnel.IsRunning)
        {
            try { await StopTunnelAsync(); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
            return;
        }
        var stats = tunnel.CaptureStatistics();
        TunnelText.Text = $"127.0.0.1:{tunnel.LocalPort} → USB port {(int)RemotePortBox.Value} · {stats.ActiveConnections} active · " +
            $"{stats.BytesToDevice:N0} bytes sent / {stats.BytesFromDevice:N0} received · {stats.FailedConnections} failed · {stats.LastFailureCategory ?? "no failure recorded"}. " +
            "Forwarding active; remote service and SSH authentication are not verified.";
    }

    private void UpdateButtons()
    {
        StartButton.IsEnabled = !_busy && _tunnel is null;
        StopButton.IsEnabled = _tunnel is not null;
        RemotePortBox.IsEnabled = !_busy && _tunnel is null;
        VerifyButton.IsEnabled = SshButton.IsEnabled = InspectButton.IsEnabled = !_busy;
        ExportButton.IsEnabled = !_busy && _inspection is not null;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    { StatusBar.Message = message; StatusBar.Severity = severity; StatusBar.IsOpen = true; }
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
