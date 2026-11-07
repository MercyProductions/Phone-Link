using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhoneLink.Integrations.AppleBluetooth.Capabilities;

namespace PhoneLink.Windows.Pages;

public sealed partial class DiagnosticsPage
{
    private CancellationTokenSource? _batteryCancellation;

    private async void BatteryListButton_Click(object sender, RoutedEventArgs args) =>
        await RunBatteryOperationAsync(async (cancellation, current) =>
        {
            BatteryDeviceSelector.ItemsSource = null;
            BluetoothBatteryText.Text = "Listing paired Bluetooth LE devices…";
            var targets = await BluetoothBatteryProbe.ListPairedAsync(cancellation);
            if (!current()) return;
            BatteryDeviceSelector.ItemsSource = targets;
            BatteryDeviceSelector.SelectedIndex = -1;
            BluetoothBatteryText.Text = targets.Count == 0
                ? "Windows returned no paired Bluetooth LE devices. No battery read was attempted."
                : "Select a device and choose Read. No battery read has been attempted.";
        });

    private async void BatteryReadButton_Click(object sender, RoutedEventArgs args)
    {
        if (BatteryDeviceSelector.SelectedItem is not PairedBatteryTarget target) return;
        await RunBatteryOperationAsync(async (cancellation, current) =>
        {
            BluetoothBatteryText.Text = "Reading the selected device’s standard Bluetooth battery service…";
            var readings = await BluetoothBatteryProbe.ReadAsync(target, cancellation);
            if (!current() || !ReferenceEquals(BatteryDeviceSelector.SelectedItem, target)) return;
            var lines = readings.Select((reading, index) => readings.Count == 1 ? DescribeBattery(reading)
                : $"Battery service {index + 1} (component not identified): {DescribeBattery(reading)}");
            BluetoothBatteryText.Text = $"Checked at {DateTimeOffset.Now:t}\n{string.Join("\n", lines)}";
        });
    }

    private async Task RunBatteryOperationAsync(Func<CancellationToken, Func<bool>, Task> action)
    {
        if (!_loaded || App.IsLocked || _batteryCancellation is not null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _batteryCancellation = cancellation;
        UpdateBatteryButtons();
        try { await action(cancellation.Token, Current); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (Current()) BluetoothBatteryText.Text = "Windows could not complete this Bluetooth check. No charge level was verified.";
        }
        finally
        {
            if (ReferenceEquals(_batteryCancellation, cancellation))
            {
                _batteryCancellation = null;
                UpdateBatteryButtons();
            }
        }

        bool Current() => _loaded && !App.IsLocked && ReferenceEquals(_batteryCancellation, cancellation);
    }

    private void BatteryDevice_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        BluetoothBatteryText.Text = "No Bluetooth battery reading for this selection.";
        UpdateBatteryButtons();
    }

    private void ClearBluetoothBattery()
    {
        _batteryCancellation?.Cancel();
        _batteryCancellation = null;
        BatteryDeviceSelector.ItemsSource = null;
        BluetoothBatteryText.Text = "No Bluetooth battery reading.";
        UpdateBatteryButtons();
    }

    private void UpdateBatteryButtons()
    {
        var ready = _loaded && !App.IsLocked && _batteryCancellation is null;
        BatteryListButton.IsEnabled = ready;
        BatteryDeviceSelector.IsEnabled = ready && BatteryDeviceSelector.Items.Count != 0;
        BatteryReadButton.IsEnabled = ready && BatteryDeviceSelector.SelectedItem is PairedBatteryTarget;
    }

    private static string DescribeBattery(BluetoothBatteryReading reading) => reading.Outcome switch
    {
        BatteryReadOutcome.Reported => $"{reading.Percent}% charge reported by the device via Bluetooth GATT.",
        BatteryReadOutcome.NotExposed => $"{reading.Stage}: no readable standard Battery Level was exposed in this check.",
        BatteryReadOutcome.AccessDenied => $"{reading.Stage}: Bluetooth access was denied.",
        BatteryReadOutcome.Unreachable => $"{reading.Stage}: Windows could not reach the device.",
        BatteryReadOutcome.ProtocolError => $"{reading.Stage}: Windows reported a Bluetooth protocol error.",
        BatteryReadOutcome.InvalidValue => "The returned value did not match the standard 0–100% format.",
        BatteryReadOutcome.Ambiguous => $"{reading.Stage}: too many or ambiguous candidates; no value was guessed.",
        _ => $"{reading.Stage}: the result was unavailable or timed out.",
    };
}
