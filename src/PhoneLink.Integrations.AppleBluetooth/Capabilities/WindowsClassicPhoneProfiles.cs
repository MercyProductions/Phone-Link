using System.Buffers.Binary;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Xml;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;

namespace PhoneLink.Integrations.AppleBluetooth.Capabilities;

public sealed class ClassicBluetoothPeer(string displayName, string deviceId)
{
    public string DisplayName { get; } = displayName;
    internal string DeviceId { get; } = deviceId;
}
public sealed record MapInboxObservation(bool Completed, string Status, IReadOnlyList<MapInboxItem> Items);
public sealed record BluetoothPhoneBookObservation(bool Completed, string Status, IReadOnlyList<BluetoothContactPreview> Items);
public sealed record BluetoothPhoneBookSizeObservation(bool Completed, string Status, ushort? ReportedEntryCount);
public sealed record BluetoothCallHistoryObservation(bool Completed, string Status, IReadOnlyList<BluetoothCallPreview> Items)
{
    internal static BluetoothCallHistoryObservation FromPermittedEntries(IReadOnlyList<BluetoothCallPreview> items) =>
        new(true, items.Count == 0
            ? "The phone answered the Bluetooth combined call-history request but shared no entries. This does not mean the iPhone’s Recents list is empty. Nothing was saved."
            : $"Received {items.Count} permitted Bluetooth call-history entry(s). Nothing was saved.", items);
}
public sealed record ClassicPhoneProfileObservation(int PairedPeers, int CheckedPeers, int PeersWithMap, int PeersWithPbap, int UnavailablePeers);

public static class WindowsClassicPhoneProfiles
{
    public const ushort PhoneBookPageSize = PbapPhoneBookClient.PageSize;
    private static readonly RfcommServiceId MessageAccess = RfcommServiceId.FromShortId(0x1132);
    private static readonly RfcommServiceId PhoneBook = RfcommServiceId.FromShortId(0x112F);

    public static async Task<IReadOnlyList<ClassicBluetoothPeer>> ListPairedAsync(CancellationToken token)
    {
        var devices = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true)).AsTask(token).ConfigureAwait(false);
        return devices.Take(64).Select(item => new ClassicBluetoothPeer(item.Name, item.Id)).ToArray();
    }

    public static async Task<ClassicPhoneProfileObservation> InspectAsync(CancellationToken token)
    {
        var peers = await ListPairedAsync(token).ConfigureAwait(false);
        int checkedPeers = 0, map = 0, phoneBook = 0;
        foreach (var peer in peers.Take(6))
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                using var device = await BluetoothDevice.FromIdAsync(peer.DeviceId).AsTask(deadline.Token).ConfigureAwait(false);
                if (device is null) continue;
                var result = await device.GetRfcommServicesAsync(BluetoothCacheMode.Uncached).AsTask(deadline.Token).ConfigureAwait(false);
                try
                {
                    if (result.Error != BluetoothError.Success) continue;
                    checkedPeers++;
                    if (result.Services.Any(service => service.ServiceId.Uuid == MessageAccess.Uuid)) map++;
                    if (result.Services.Any(service => service.ServiceId.Uuid == PhoneBook.Uuid)) phoneBook++;
                }
                finally { foreach (var service in result.Services) service.Dispose(); }
            }
            catch (Exception exception) when (!token.IsCancellationRequested && exception is not OutOfMemoryException) { }
        }
        return new(peers.Count, checkedPeers, map, phoneBook, peers.Count - checkedPeers);
    }

    public static async Task<MapInboxObservation> ReadInboxAsync(ClassicBluetoothPeer selected, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(selected);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var bounded = deadline.Token;
        try
        {
            using var device = await BluetoothDevice.FromIdAsync(selected.DeviceId).AsTask(bounded).ConfigureAwait(false);
            if (device is null || !device.DeviceInformation.Pairing.IsPaired)
                return Unavailable("Windows cannot access the selected paired Bluetooth device.");
            var result = await device.GetRfcommServicesForIdAsync(MessageAccess, BluetoothCacheMode.Uncached).AsTask(bounded).ConfigureAwait(false);
            try
            {
                if (result.Error != BluetoothError.Success) return Unavailable($"Windows did not complete MAP discovery ({result.Error}).");
                if (result.Services.Count == 0) return Unavailable("The selected device did not expose a MAP message server through Windows RFCOMM. No messages were requested.");
                if (result.Services.Count > 8) return Unavailable("Too many MAP servers were reported. No inbox was opened.");
                List<(RfcommDeviceService Service, bool ConnectFeatures)> candidates = [];
                foreach (var candidate in result.Services)
                {
                    var attributes = await candidate.GetSdpRawAttributesAsync(BluetoothCacheMode.Cached).AsTask(bounded).ConfigureAwait(false);
                    var messageTypes = attributes.TryGetValue(0x0316, out var typeValue) && typeValue.Length == 2 ? typeValue.ToArray() : [];
                    var hasMessageTypes = messageTypes.Length == 2 && messageTypes[0] == 0x08;
                    if (hasMessageTypes ? (messageTypes[1] & 0x1E) == 0 : result.Services.Count != 1) continue;
                    var supportsConnectFeatures = attributes.TryGetValue(0x0317, out var featureValue) && featureValue.Length == 5 &&
                        ReadConnectFeatures(featureValue.ToArray());
                    candidates.Add((candidate, supportsConnectFeatures));
                }
                if (candidates.Count != 1) return Unavailable("No unambiguous text-message server was identified. No inbox was opened.");
                var (service, connectFeatures) = candidates[0];
                using var socket = new StreamSocket();
                await socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName,
                    SocketProtectionLevel.BluetoothEncryptionWithAuthentication).AsTask(bounded).ConfigureAwait(false);
                using var input = socket.InputStream.AsStreamForRead();
                using var output = socket.OutputStream.AsStreamForWrite();
                var items = await MapInboxClient.ReadAsync(input, output, connectFeatures, bounded).ConfigureAwait(false);
                return new(true, $"Received {items.Count} permitted inbox preview(s) over Bluetooth MAP. This is not full Messages history.", items);
            }
            finally { foreach (var service in result.Services) service.Dispose(); }
        }
        catch (MapInboxRejectedException exception) when (!token.IsCancellationRequested)
        {
            return Unavailable(exception.Describe());
        }
        catch (UnauthorizedAccessException) when (!token.IsCancellationRequested)
        { return Unavailable("Windows denied access to the selected Bluetooth device or MAP service."); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return Unavailable("The Bluetooth inbox check timed out. The phone’s message availability is unknown."); }
        catch (Exception exception) when (!token.IsCancellationRequested && exception is IOException or XmlException or InvalidDataException)
        { return Unavailable("The MAP channel did not return a complete supported inbox listing."); }
    }

    public static async Task<BluetoothPhoneBookObservation> ReadPhoneBookAsync(ClassicBluetoothPeer selected, ushort startOffset, CancellationToken token)
    {
        var result = await ReadPbapAsync(selected, (input, output, features, bounded) =>
            PbapPhoneBookClient.ReadAsync(input, output, startOffset, features, bounded), token).ConfigureAwait(false);
        var items = result.Value ?? [];
        return new(result.Completed, result.Completed
            ? $"Received {items.Count} permitted Bluetooth contact(s). Nothing was saved or merged."
            : result.Status, items);
    }

    public static async Task<BluetoothPhoneBookSizeObservation> CheckPhoneBookSizeAsync(ClassicBluetoothPeer selected, CancellationToken token)
    {
        var result = await ReadPbapAsync(selected, PbapPhoneBookClient.ReadSizeAsync, token).ConfigureAwait(false);
        return new(result.Completed, result.Completed
            ? $"The phone reported {result.Value} phone-book entries over Bluetooth. No contact records were requested. A count does not verify access to contact details."
            : result.Status + " No fallback contact read was attempted.", result.Value);
    }

    public static async Task<BluetoothCallHistoryObservation> ReadCallHistoryAsync(ClassicBluetoothPeer selected, CancellationToken token)
    {
        var result = await ReadPbapAsync(selected, PbapPhoneBookClient.ReadCallHistoryAsync, token).ConfigureAwait(false);
        return result.Completed ? BluetoothCallHistoryObservation.FromPermittedEntries(result.Value ?? [])
            : new(false, result.Status, []);
    }

    private static async Task<(bool Completed, string Status, T? Value)> ReadPbapAsync<T>(ClassicBluetoothPeer selected,
        Func<Stream, Stream, bool, CancellationToken, Task<T>> read, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(selected);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var bounded = deadline.Token;
        try
        {
            using var device = await BluetoothDevice.FromIdAsync(selected.DeviceId).AsTask(bounded).ConfigureAwait(false);
            if (device is null || !device.DeviceInformation.Pairing.IsPaired)
                return Failure("Windows cannot access the selected paired Bluetooth device.");
            var result = await device.GetRfcommServicesForIdAsync(PhoneBook, BluetoothCacheMode.Uncached).AsTask(bounded).ConfigureAwait(false);
            try
            {
                if (result.Error != BluetoothError.Success) return Failure($"Windows did not complete PBAP discovery ({result.Error}).");
                if (result.Services.Count != 1) return Failure(result.Services.Count == 0
                    ? "The selected phone did not expose a Bluetooth phone-book server through Windows RFCOMM."
                    : "Multiple phone-book servers were reported; no service was selected automatically.");
                var service = result.Services[0];
                var attributes = await service.GetSdpRawAttributesAsync(BluetoothCacheMode.Cached).AsTask(bounded).ConfigureAwait(false);
                using var socket = new StreamSocket();
                await socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName,
                    SocketProtectionLevel.BluetoothEncryptionWithAuthentication).AsTask(bounded).ConfigureAwait(false);
                using var input = socket.InputStream.AsStreamForRead();
                using var output = socket.OutputStream.AsStreamForWrite();
                var items = await read(input, output, attributes.ContainsKey(0x0317), bounded).ConfigureAwait(false);
                return (true, string.Empty, items);
            }
            finally { foreach (var service in result.Services) service.Dispose(); }
        }
        catch (PhoneObexRejectedException exception) when (!token.IsCancellationRequested)
        { return Failure($"The PBAP server rejected the read (OBEX 0x{exception.Code:X2}). The requested data may not be shared on this pairing."); }
        catch (UnauthorizedAccessException) when (!token.IsCancellationRequested)
        { return Failure("Windows denied access to this Bluetooth phone-book service."); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return Failure("The PBAP read timed out. Data availability is unknown."); }
        catch (Exception exception) when (!token.IsCancellationRequested && exception is IOException or InvalidDataException or System.Text.DecoderFallbackException)
        { return Failure("The PBAP channel did not return a complete supported response."); }

        static (bool, string, T?) Failure(string reason) => (false, reason, default);
    }

    private static bool ReadConnectFeatures(byte[] data) => data[0] == 0x0A && (BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(1)) & (1u << 19)) != 0;
    private static MapInboxObservation Unavailable(string status) => new(false, status, []);
}
