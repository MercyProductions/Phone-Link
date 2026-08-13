using PhoneLink.DeviceCommunication.Discovery;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class WindowsDnsSdAdvertiserTests
{
    [Fact]
    public async Task RegistersAndDeregistersProcessScopedBonjourService()
    {
        await using var advertiser = await WindowsDnsSdAdvertiser.StartAsync(
            $"Phone Link Test {Guid.NewGuid():N}",
            54_321,
            CancellationToken.None);
    }
}
