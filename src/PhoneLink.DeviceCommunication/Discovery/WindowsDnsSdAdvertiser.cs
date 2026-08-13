using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace PhoneLink.DeviceCommunication.Discovery;

public sealed class WindowsDnsSdAdvertiser : IAsyncDisposable
{
    private const uint DnsQueryRequestVersion1 = 1;
    private const uint DnsRequestPending = 9506;
    private readonly RegisterCompleteCallback _callback;
    private readonly TaskCompletionSource _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _deregistered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IntPtr _serviceInstance;
    private IntPtr _request;
    private bool _isDeregistering;
    private bool _disposed;

    private WindowsDnsSdAdvertiser()
    {
        _callback = OnRegistrationCompleted;
    }

    public static async Task<WindowsDnsSdAdvertiser> StartAsync(
        string instanceName,
        int port,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        if (port is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        var advertiser = new WindowsDnsSdAdvertiser();
        try
        {
            advertiser.Start(instanceName, port);
            await advertiser._registered.Task.WaitAsync(
                TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);
            return advertiser;
        }
        catch
        {
            await advertiser.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_request != IntPtr.Zero && _registered.Task.IsCompletedSuccessfully)
        {
            _isDeregistering = true;
            var status = DnsServiceDeRegister(_request, IntPtr.Zero);
            if (status == DnsRequestPending)
            {
                try
                {
                    await _deregistered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                }
            }
        }

        if (_serviceInstance != IntPtr.Zero)
        {
            DnsServiceFreeInstance(_serviceInstance);
            _serviceInstance = IntPtr.Zero;
        }
        if (_request != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_request);
            _request = IntPtr.Zero;
        }
    }

    private void Start(string instanceName, int port)
    {
        var serviceName = $"{SanitizeInstanceName(instanceName)}._phonelink._tcp.local";
        _serviceInstance = DnsServiceConstructInstance(
            serviceName,
            $"{Dns.GetHostName()}.local",
            IntPtr.Zero,
            IntPtr.Zero,
            checked((ushort)port),
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero);
        if (_serviceInstance == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "DNS-SD service construction failed.");
        }

        var request = new DnsServiceRegisterRequest
        {
            Version = DnsQueryRequestVersion1,
            InterfaceIndex = 0,
            ServiceInstance = _serviceInstance,
            RegisterCompletionCallback = Marshal.GetFunctionPointerForDelegate(_callback),
            QueryContext = IntPtr.Zero,
            Credentials = IntPtr.Zero,
            UnicastEnabled = 0,
        };
        _request = Marshal.AllocHGlobal(Marshal.SizeOf<DnsServiceRegisterRequest>());
        Marshal.StructureToPtr(request, _request, fDeleteOld: false);
        var status = DnsServiceRegister(_request, IntPtr.Zero);
        if (status != DnsRequestPending)
        {
            throw new Win32Exception(checked((int)status), "DNS-SD service registration failed.");
        }
    }

    private void OnRegistrationCompleted(uint status, IntPtr queryContext, IntPtr instance)
    {
        if (instance != IntPtr.Zero)
        {
            DnsServiceFreeInstance(instance);
        }

        var completion = _isDeregistering ? _deregistered : _registered;
        if (status == 0)
        {
            completion.TrySetResult();
        }
        else
        {
            completion.TrySetException(
                new Win32Exception(checked((int)status), "The DNS-SD operation failed."));
        }
    }

    private static string SanitizeInstanceName(string value)
    {
        var sanitized = new string(value
            .Trim()
            .Where(character => !char.IsControl(character) && character is not '.' and not '\\')
            .Take(50)
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "Phone Link" : sanitized;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DnsServiceRegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr ServiceInstance;
        public IntPtr RegisterCompletionCallback;
        public IntPtr QueryContext;
        public IntPtr Credentials;
        public int UnicastEnabled;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void RegisterCompleteCallback(uint status, IntPtr queryContext, IntPtr instance);

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr DnsServiceConstructInstance(
        string serviceName,
        string? hostName,
        IntPtr ip4,
        IntPtr ip6,
        ushort port,
        ushort priority,
        ushort weight,
        uint propertyCount,
        IntPtr keys,
        IntPtr values);

    [DllImport("dnsapi.dll")]
    private static extern uint DnsServiceRegister(IntPtr request, IntPtr cancel);

    [DllImport("dnsapi.dll")]
    private static extern uint DnsServiceDeRegister(IntPtr request, IntPtr cancel);

    [DllImport("dnsapi.dll")]
    private static extern void DnsServiceFreeInstance(IntPtr instance);
}
