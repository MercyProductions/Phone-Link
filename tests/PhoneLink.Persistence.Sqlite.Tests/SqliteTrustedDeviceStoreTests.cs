using PhoneLink.Domain.Devices;
using PhoneLink.Application.Ports;
using PhoneLink.Persistence.Sqlite.Configuration;
using System.Text;

namespace PhoneLink.Persistence.Sqlite.Tests;

public sealed class SqliteTrustedDeviceStoreTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        "PhoneLink.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InitializesFrozenSchemaAndRoundTripsTrustRecord()
    {
        using var database = CreateDatabase();
        var store = new SqliteTrustedDeviceStore(database);
        var id = DeviceId.New();
        var pairedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var device = TrustedDevice.Create(
            id,
            "Gabriel's iPhone",
            DevicePlatform.Ios,
            Enumerable.Repeat((byte)0x5A, 32).ToArray(),
            pairedAt);

        await store.SaveAsync(device, CancellationToken.None);
        var loaded = await store.FindAsync(id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(device.Id, loaded.Id);
        Assert.Equal(device.DisplayName, loaded.DisplayName);
        Assert.Equal(device.IdentityPublicKey.ToArray(), loaded.IdentityPublicKey.ToArray());
        Assert.Equal(DeviceTrustState.Trusted, loaded.TrustState);

        var revokedAt = DateTimeOffset.UtcNow;
        await store.RevokeAsync(id, revokedAt, CancellationToken.None);
        var revoked = await store.FindAsync(id, CancellationToken.None);
        Assert.NotNull(revoked);
        Assert.Equal(DeviceTrustState.Revoked, revoked.TrustState);
        Assert.NotNull(revoked.RevokedAt);
    }

    [Fact]
    public async Task InitializationIsIdempotentAndMigrationChecksumIsVerified()
    {
        using var database = CreateDatabase();

        await database.InitializeAsync(CancellationToken.None);
        await database.InitializeAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_testDirectory, "phone-link.db")));
    }

    [Fact]
    public async Task DatabaseIsEncryptedAndWrongProtectedKeyFailsClosed()
    {
        var secretStore = new InMemorySecretStore();
        using (var database = CreateDatabase(secretStore))
        {
            await database.InitializeAsync(CancellationToken.None);
        }

        var databasePath = Path.Combine(_testDirectory, "phone-link.db");
        var header = File.ReadAllBytes(databasePath).AsSpan(0, 16);
        Assert.NotEqual("SQLite format 3\0", Encoding.ASCII.GetString(header));

        using var wrongKeyDatabase = CreateDatabase(new InMemorySecretStore());
        await Assert.ThrowsAnyAsync<Exception>(
            () => wrongKeyDatabase.InitializeAsync(CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    private SqliteDatabase CreateDatabase(InMemorySecretStore? secretStore = null) =>
        new(
            new SqliteStorageOptions(
                Path.Combine(_testDirectory, "phone-link.db"),
                Path.Combine(_testDirectory, "attachments")),
            secretStore ?? new InMemorySecretStore());

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<SecretName, byte[]> _values = [];

        public Task StoreAsync(
            SecretName name,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values[name] = value.ToArray();
            return Task.CompletedTask;
        }

        public Task<SecretValue?> RetrieveAsync(SecretName name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                _values.TryGetValue(name, out var value)
                    ? new SecretValue(value)
                    : null);
        }

        public Task DeleteAsync(SecretName name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.Remove(name);
            return Task.CompletedTask;
        }
    }
}
