using PhoneLink.Application.Features.Calling;
using Windows.ApplicationModel.Calls;
using Windows.Devices.Enumeration;
using Windows.Foundation.Metadata;

namespace PhoneLink.Integrations.AppleBluetooth.Calling;

public static class WindowsCallTransportProbe
{
    private const string ApiType = "Windows.ApplicationModel.Calls.PhoneLineTransportDevice";

    public static async Task<CallTransportInspection> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!ApiInformation.IsTypePresent(ApiType)) return Unknown(CallTransportQueryState.ApiUnavailable);
            var selector = PhoneLineTransportDevice.GetDeviceSelector(PhoneLineTransport.Bluetooth);
            var devices = await DeviceInformation.FindAllAsync(selector).AsTask(cancellationToken).ConfigureAwait(false);
            List<CallTransportObservation> observations = [];
            foreach (var device in devices.Take(32))
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool? registered = null;
                var routing = CallAudioRoutingObservation.Unknown;
                var denied = false;
                try
                {
                    var transport = PhoneLineTransportDevice.FromId(device.Id);
                    if (transport is not null)
                    {
                        try { registered = transport.IsRegistered(); }
                        catch (Exception exception) when (exception is not OutOfMemoryException)
                        { denied |= IsDenied(exception); }
                        if (ApiInformation.IsPropertyPresent(ApiType, nameof(PhoneLineTransportDevice.AudioRoutingStatus)))
                        {
                            try
                            {
                                routing = transport.AudioRoutingStatus switch
                                {
                                    TransportDeviceAudioRoutingStatus.CanRouteToLocalDevice => CallAudioRoutingObservation.CanRouteToPc,
                                    TransportDeviceAudioRoutingStatus.CannotRouteToLocalDevice => CallAudioRoutingObservation.CannotRouteToPc,
                                    _ => CallAudioRoutingObservation.Unknown,
                                };
                            }
                            catch (Exception exception) when (exception is not OutOfMemoryException)
                            { denied |= IsDenied(exception); }
                        }
                    }
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { denied |= IsDenied(exception); }
                observations.Add(new(registered, routing, denied));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(DateTimeOffset.UtcNow, CallTransportQueryState.Completed, devices.Count, observations);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Unknown(IsDenied(exception) ? CallTransportQueryState.AccessDenied : CallTransportQueryState.Unavailable); }
    }

    private static bool IsDenied(Exception exception) => exception.HResult == unchecked((int)0x80070005);
    private static CallTransportInspection Unknown(CallTransportQueryState state) => new(DateTimeOffset.UtcNow, state, null, []);
}
