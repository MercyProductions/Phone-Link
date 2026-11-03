using PhoneLink.Application.Features.Calling;
using Windows.ApplicationModel.Calls;
using Windows.Devices.Enumeration;
using Windows.Foundation.Metadata;

namespace PhoneLink.Integrations.AppleBluetooth.Calling;

public sealed class CallTransportCandidate(string id, string displayName)
{
    public string Id { get; } = id;
    public string DisplayName { get; } = displayName;
    public override string ToString() => DisplayName;
}

public sealed class WindowsCallTransportControl : ICallTransportControl
{
    private readonly PhoneLineTransportDevice _transport;
    private WindowsCallTransportControl(PhoneLineTransportDevice transport) => _transport = transport;

    // Enumeration only. No access request or persistent action runs during discovery.
    public static async Task<IReadOnlyList<CallTransportCandidate>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ApiInformation.IsTypePresent("Windows.ApplicationModel.Calls.PhoneLineTransportDevice"))
            throw new NotSupportedException("Windows phone-line transport API is unavailable.");
        var devices = await DeviceInformation.FindAllAsync(PhoneLineTransportDevice.GetDeviceSelector(PhoneLineTransport.Bluetooth))
            .AsTask(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (devices.Count > 32) throw new InvalidOperationException("Too many endpoints; narrow the Windows Bluetooth configuration before selecting a transport.");
        return devices.Select(device =>
        {
            var name = new string(device.Name.Where(character => !char.IsControl(character)).Take(100).ToArray());
            return new CallTransportCandidate(device.Id, string.IsNullOrWhiteSpace(name) ? "Unnamed Bluetooth phone transport" : name);
        }).ToArray();
    }

    public static CallTransportController Create(CallTransportCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var transport = PhoneLineTransportDevice.FromId(candidate.Id)
            ?? throw new InvalidOperationException("The selected transport is no longer available.");
        if (transport.Transport != PhoneLineTransport.Bluetooth) throw new InvalidOperationException("The selected transport is not Bluetooth.");
        return new(new WindowsCallTransportControl(transport));
    }

    public bool IsRegistered() => _transport.IsRegistered();
    public async Task<CallTransportAccess> RequestAccessAsync(CancellationToken cancellationToken) =>
        await _transport.RequestAccessAsync().AsTask(cancellationToken) switch
        {
            DeviceAccessStatus.Allowed => CallTransportAccess.Allowed,
            DeviceAccessStatus.DeniedByUser => CallTransportAccess.DeniedByUser,
            DeviceAccessStatus.DeniedBySystem => CallTransportAccess.DeniedBySystem,
            _ => CallTransportAccess.Unknown,
        };
    public void Register() => _transport.RegisterApp();
    public void Unregister() => _transport.UnregisterApp();
    public async Task<bool> ConnectAsync(CancellationToken cancellationToken) => await _transport.ConnectAsync().AsTask(cancellationToken);
}
