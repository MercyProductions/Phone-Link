namespace PhoneLink.Application.Ports;

public interface IDeviceTransport : IAsyncDisposable
{
    TransportKind Kind { get; }

    TransportState State { get; }

    IAsyncEnumerable<TransportStateChanged> StateChanges(
        CancellationToken cancellationToken);

    Task ConnectAsync(
        DeviceConnectionContext context,
        CancellationToken cancellationToken);

    ValueTask SendAsync(
        ReadOnlyMemory<byte> frame,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);
}

