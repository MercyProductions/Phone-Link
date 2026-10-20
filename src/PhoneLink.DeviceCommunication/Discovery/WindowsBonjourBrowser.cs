using Windows.Devices.Enumeration;

namespace PhoneLink.DeviceCommunication.Discovery;

public static class WindowsBonjourBrowser
{
    private static readonly string[] ServiceTypes = ["_phonelink._tcp", "_airplay._tcp", "_raop._tcp"];
    private static readonly string[] RequestedProperties =
    [
        "System.Devices.Dnssd.ServiceName", "System.Devices.Dnssd.InstanceName",
        "System.Devices.Dnssd.HostName", "System.Devices.Dnssd.PortNumber", "System.Devices.IpAddress",
    ];

    public static async Task<IReadOnlyList<BonjourServiceObservation>> DiscoverAsync(
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        if (duration < TimeSpan.FromMilliseconds(100) || duration > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var results = await Task.WhenAll(ServiceTypes.Select(serviceType =>
            BrowseServiceAsync(serviceType, duration, cancellationToken))).ConfigureAwait(false);
        return results.SelectMany(result => result)
            .OrderBy(result => result.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.ServiceType, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<BonjourServiceObservation[]> BrowseServiceAsync(
        string serviceType,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var selector = "System.Devices.AepService.ProtocolId:=\"{4526e8c1-8aac-4153-9b16-55e86ada0e54}\" " +
            $"AND System.Devices.Dnssd.ServiceName:=\"{serviceType}\" AND System.Devices.Dnssd.Domain:=\"local\"";
        var watcher = DeviceInformation.CreateWatcher(selector, RequestedProperties,
            DeviceInformationKind.AssociationEndpointService);
        var gate = new object();
        var devices = new Dictionary<string, DeviceInformation>(StringComparer.Ordinal);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void Added(DeviceWatcher sender, DeviceInformation device)
        {
            lock (gate)
            {
                if (devices.Count < 128 || devices.ContainsKey(device.Id)) { devices[device.Id] = device; }
            }
        }
        void Updated(DeviceWatcher sender, DeviceInformationUpdate update)
        {
            lock (gate)
            {
                if (devices.TryGetValue(update.Id, out var device)) { device.Update(update); }
            }
        }
        void Removed(DeviceWatcher sender, DeviceInformationUpdate update)
        {
            lock (gate) { devices.Remove(update.Id); }
        }
        void Stopped(DeviceWatcher sender, object args) => stopped.TrySetResult();

        watcher.Added += Added;
        watcher.Updated += Updated;
        watcher.Removed += Removed;
        watcher.Stopped += Stopped;
        try
        {
            watcher.Start();
            await Task.WhenAny(Task.Delay(duration, cancellationToken), stopped.Task).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (watcher.Status == DeviceWatcherStatus.Aborted)
            {
                throw new InvalidOperationException("Windows stopped local service discovery. Check network availability and app permissions.");
            }
            lock (gate)
            {
                return devices.Values.Select(device => BonjourServiceObservation.FromProperties(
                        device.Id, device.Name, device.Properties, DateTimeOffset.UtcNow))
                    .OfType<BonjourServiceObservation>()
                    .ToArray();
            }
        }
        finally
        {
            try
            {
                if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                {
                    watcher.Stop();
                    // Cleanup must finish even if the caller cancelled the scan.
                    await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                watcher.Added -= Added;
                watcher.Updated -= Updated;
                watcher.Removed -= Removed;
                watcher.Stopped -= Stopped;
            }
        }
    }
}
