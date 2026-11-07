using PhoneLink.Integrations.AppleBluetooth.Capabilities;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class BluetoothBatteryProbeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(57)]
    [InlineData(100)]
    public void AcceptsAStandardSingleBytePercentage(int value)
    {
        var result = BluetoothBatteryProbe.Decode([(byte)value]);
        Assert.Equal(new BluetoothBatteryReading(BatteryReadStage.Value, BatteryReadOutcome.Reported, value), result);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 50, 80 })]
    [InlineData(new byte[] { 101 })]
    [InlineData(new byte[] { 255 })]
    public void RejectsMalformedOrReservedValues(byte[] value)
    {
        var result = BluetoothBatteryProbe.Decode(value);
        Assert.Equal(BatteryReadOutcome.InvalidValue, result.Outcome);
        Assert.Null(result.Percent);
    }

    [Theory]
    [InlineData(GattCommunicationStatus.AccessDenied, BatteryReadOutcome.AccessDenied)]
    [InlineData(GattCommunicationStatus.Unreachable, BatteryReadOutcome.Unreachable)]
    [InlineData(GattCommunicationStatus.ProtocolError, BatteryReadOutcome.ProtocolError)]
    public void CommunicationErrorsNeverBecomePercentages(GattCommunicationStatus status, BatteryReadOutcome expected)
    {
        var result = BluetoothBatteryProbe.Failure(BatteryReadStage.Characteristic, status);
        Assert.Equal(new BluetoothBatteryReading(BatteryReadStage.Characteristic, expected), result);
    }

    [Fact]
    public async Task PreCancelledReadDoesNotOpenADevice()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BluetoothBatteryProbe.ReadAsync(new PairedBatteryTarget("unused-test-id", "Synthetic accessory"), cancellation.Token));
    }
}
