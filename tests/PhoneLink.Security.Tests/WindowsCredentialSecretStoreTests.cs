using System.Text;
using PhoneLink.Application.Ports;
using PhoneLink.Security.Storage;

namespace PhoneLink.Security.Tests;

public sealed class WindowsCredentialSecretStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "PhoneLink.DpapiTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ProtectsSecretToLocalUserAndRoundTrips()
    {
        var name = new SecretName("test/database-key");
        var plaintext = Encoding.UTF8.GetBytes("not-visible-in-the-protected-file");
        using var store = new WindowsCredentialSecretStore(_directory);

        await store.StoreAsync(name, plaintext, CancellationToken.None);
        using var retrieved = await store.RetrieveAsync(name, CancellationToken.None);

        Assert.NotNull(retrieved);
        Assert.Equal(plaintext, retrieved.Bytes.ToArray());
        var protectedFile = Assert.Single(Directory.GetFiles(_directory, "*.bin"));
        Assert.DoesNotContain(
            "not-visible-in-the-protected-file",
            Encoding.UTF8.GetString(await File.ReadAllBytesAsync(protectedFile)));

        await store.DeleteAsync(name, CancellationToken.None);
        Assert.Null(await store.RetrieveAsync(name, CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
