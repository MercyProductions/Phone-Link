using PhoneLink.Application.Features.Synchronization;
using PhoneLink.Application.Ports;
using PhoneLink.Domain.Devices;
using PhoneLink.Persistence.Sqlite.Configuration;

namespace PhoneLink.Persistence.Sqlite.Tests;

public sealed class SqliteInboundSyncReceiptStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "PhoneLink.ReceiptTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RecordsExactRetryAndRejectsSequenceReuse()
    {
        using var database = new SqliteDatabase(
            new SqliteStorageOptions(
                Path.Combine(_directory, "phone-link.db"),
                Path.Combine(_directory, "attachments")),
            new InMemorySecretStore());
        var deviceId = DeviceId.New();
        await new SqliteTrustedDeviceStore(database).SaveAsync(
            TrustedDevice.Create(
                deviceId,
                "Test iPhone",
                DevicePlatform.Ios,
                Enumerable.Repeat((byte)0x55, 32).ToArray(),
                DateTimeOffset.UtcNow),
            CancellationToken.None);
        var store = new SqliteInboundSyncReceiptStore(database);
        var receipt = CreateReceipt(deviceId, Guid.NewGuid(), 1, 0x41);

        Assert.Equal(
            SyncReceiptMatch.Missing,
            await store.MatchAsync(receipt, CancellationToken.None));
        await store.RecordAppliedAsync(receipt, CancellationToken.None);
        Assert.Equal(
            SyncReceiptMatch.ExactApplied,
            await store.MatchAsync(receipt, CancellationToken.None));
        Assert.Equal(
            1,
            await store.GetHighestAppliedSequenceAsync(deviceId, CancellationToken.None));

        var conflict = CreateReceipt(deviceId, Guid.NewGuid(), 1, 0x42);
        Assert.Equal(
            SyncReceiptMatch.Conflict,
            await store.MatchAsync(conflict, CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static InboundSyncReceipt CreateReceipt(
        DeviceId deviceId,
        Guid eventId,
        long sequence,
        byte hashByte) =>
        new(
            eventId,
            deviceId,
            sequence,
            "contact.upsert",
            "contact-1",
            DateTimeOffset.UtcNow,
            "{}",
            Enumerable.Repeat(hashByte, 32).ToArray(),
            DateTimeOffset.UtcNow);

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<SecretName, byte[]> _values = [];

        public Task StoreAsync(
            SecretName name,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken)
        {
            _values[name] = value.ToArray();
            return Task.CompletedTask;
        }

        public Task<SecretValue?> RetrieveAsync(
            SecretName name,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                _values.TryGetValue(name, out var value) ? new SecretValue(value) : null);

        public Task DeleteAsync(SecretName name, CancellationToken cancellationToken)
        {
            _values.Remove(name);
            return Task.CompletedTask;
        }
    }
}
