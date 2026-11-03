using PhoneLink.Application.Features.Media;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace PhoneLink.Integrations.AppleBluetooth.Audio;

public sealed record RemoteAudioCandidate(string Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed class WindowsRemoteAudioConnection : IRemoteAudioConnection
{
    private readonly AudioPlaybackConnection _connection;
    private WindowsRemoteAudioConnection(AudioPlaybackConnection connection) => _connection = connection;

    public static async Task<IReadOnlyList<RemoteAudioCandidate>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var devices = await DeviceInformation.FindAllAsync(AudioPlaybackConnection.GetDeviceSelector()).AsTask(cancellationToken);
        return devices.Select(device => new RemoteAudioCandidate(device.Id,
            string.IsNullOrWhiteSpace(device.Name) ? "Unnamed remote audio source" :
                new string(device.Name.Where(character => !char.IsControl(character)).Take(100).ToArray()))).ToArray();
    }

    public static RemoteAudioSession Create(RemoteAudioCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var connection = AudioPlaybackConnection.TryCreateFromId(candidate.Id)
            ?? throw new InvalidOperationException("Windows could not create this audio receiver connection.");
        return new RemoteAudioSession(new WindowsRemoteAudioConnection(connection));
    }

    public RemoteAudioState State => _connection.State switch
    {
        AudioPlaybackConnectionState.Closed => RemoteAudioState.Closed,
        AudioPlaybackConnectionState.Opened => RemoteAudioState.Opened,
        _ => RemoteAudioState.Unknown,
    };

    public async Task EnableAsync(CancellationToken cancellationToken) =>
        await _connection.StartAsync().AsTask(cancellationToken);

    public async Task<RemoteAudioOpenOutcome> OpenAsync(CancellationToken cancellationToken) =>
        (await _connection.OpenAsync().AsTask(cancellationToken)).Status switch
        {
            AudioPlaybackConnectionOpenResultStatus.Success => RemoteAudioOpenOutcome.Opened,
            AudioPlaybackConnectionOpenResultStatus.RequestTimedOut => RemoteAudioOpenOutcome.TimedOut,
            AudioPlaybackConnectionOpenResultStatus.DeniedBySystem => RemoteAudioOpenOutcome.DeniedBySystem,
            _ => RemoteAudioOpenOutcome.UnknownFailure,
        };

    public void Dispose() => _connection.Dispose();
}
