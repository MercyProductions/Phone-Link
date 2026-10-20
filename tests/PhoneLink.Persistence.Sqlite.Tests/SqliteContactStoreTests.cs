using PhoneLink.Application.Features.Contacts;
using PhoneLink.Application.Ports;
using PhoneLink.Application.Features.Synchronization;
using PhoneLink.Domain.Devices;
using PhoneLink.Persistence.Sqlite.Configuration;

namespace PhoneLink.Persistence.Sqlite.Tests;

public sealed class SqliteContactStoreTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        "PhoneLink.ContactTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReconciliationScrubsUnauthorizedFieldsButPreservesOtherDevicesAndReceipts()
    {
        using var database = new SqliteDatabase(new SqliteStorageOptions(
            Path.Combine(_testDirectory, "phone-link.db"), Path.Combine(_testDirectory, "attachments")), new InMemorySecretStore());
        var owner = DeviceId.New();
        var other = DeviceId.New();
        foreach (var id in new[] { owner, other })
        {
            await new SqliteTrustedDeviceStore(database).SaveAsync(TrustedDevice.Create(id,
                "Test iPhone", DevicePlatform.Ios,
                Enumerable.Repeat(id == owner ? (byte)0x11 : (byte)0x22, 32).ToArray(), DateTimeOffset.UtcNow), CancellationToken.None);
        }
        var contact = new SyncedContact(Guid.NewGuid().ToString("D"), owner, "revoked-contact",
            "Private Contact", "Private", "Contact", "Organization",
            [new ContactMethod("mobile", "5550100", "5550100")], [], false, DateTimeOffset.UtcNow);
        var store = new SqliteContactStore(database);
        await store.UpsertAsync(contact, CancellationToken.None);
        await store.UpsertAsync(contact with { ContactId = Guid.NewGuid().ToString("D"), OwnerDeviceId = other }, CancellationToken.None);
        var receipts = new SqliteInboundSyncReceiptStore(database);
        var receipt = new InboundSyncReceipt(Guid.NewGuid(), owner, 1, "contact.upsert", contact.SourceContactId,
            DateTimeOffset.UtcNow, "{\"displayName\":\"Private Contact\"}", new byte[32], DateTimeOffset.UtcNow);
        await receipts.RecordAppliedAsync(receipt, CancellationToken.None);

        await store.ReconcileAuthorizationAsync(owner, [], DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Empty(await store.ListAsync(owner, null, 20, CancellationToken.None));
        Assert.Single(await store.ListAsync(other, null, 20, CancellationToken.None));
        Assert.Equal(SyncReceiptMatch.ExactApplied, await receipts.MatchAsync(receipt, CancellationToken.None));
        Assert.Equal(1, await receipts.GetHighestAppliedSequenceAsync(owner, CancellationToken.None));
        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT display_name || given_name || family_name || organization_name FROM contacts WHERE contact_id=$id";
        command.Parameters.AddWithValue("$id", contact.ContactId);
        Assert.Equal("", await command.ExecuteScalarAsync());
        command.CommandText = "SELECT COUNT(*) FROM contact_methods WHERE contact_id=$id";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
        command.Parameters.Clear();
        command.CommandText = "SELECT payload_json FROM sync_events WHERE event_id=$event";
        command.Parameters.AddWithValue("$event", receipt.EventId.ToString("D"));
        Assert.Equal("{}", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task UpsertsSearchesAndDeletesAuthorizedContact()
    {
        using var database = new SqliteDatabase(
            new SqliteStorageOptions(
                Path.Combine(_testDirectory, "phone-link.db"),
                Path.Combine(_testDirectory, "attachments")),
            new InMemorySecretStore());
        var deviceId = DeviceId.New();
        await new SqliteTrustedDeviceStore(database).SaveAsync(
            TrustedDevice.Create(
                deviceId,
                "Test iPhone",
                DevicePlatform.Ios,
                Enumerable.Repeat((byte)0x3C, 32).ToArray(),
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        var store = new SqliteContactStore(database);
        var contact = new SyncedContact(
            Guid.NewGuid().ToString("D"),
            deviceId,
            "ios-contact-1",
            "Ada Lovelace",
            "Ada",
            "Lovelace",
            "Analytical Engines",
            [new ContactMethod("mobile", "+1 (312) 555-0101", "+13125550101")],
            [new ContactMethod("work", "ada@example.test", "ada@example.test")],
            false,
            DateTimeOffset.UtcNow);

        await store.UpsertAsync(contact, CancellationToken.None);
        var results = await store.ListAsync(deviceId, "Lovelace", 20, CancellationToken.None);

        var saved = Assert.Single(results);
        Assert.Equal("Ada Lovelace", saved.DisplayName);
        Assert.Equal("+13125550101", Assert.Single(saved.PhoneNumbers).NormalizedValue);
        Assert.Equal("ada@example.test", Assert.Single(saved.EmailAddresses).NormalizedValue);

        var updated = contact with
        {
            DisplayName = "Ada King",
            PhoneNumbers = [new ContactMethod("home", "312-555-0199", "3125550199")],
            EmailAddresses = [],
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1),
        };
        await store.UpsertAsync(updated, CancellationToken.None);
        var updatedResults = await store.ListAsync(deviceId, "Ada", 20, CancellationToken.None);
        var savedUpdate = Assert.Single(updatedResults);
        Assert.Equal("Ada King", savedUpdate.DisplayName);
        Assert.Empty(savedUpdate.EmailAddresses);

        await store.DeleteAsync(deviceId, contact.SourceContactId, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Empty(await store.ListAsync(deviceId, null, 20, CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<SecretName, byte[]> _values = [];

        public Task StoreAsync(SecretName name, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values[name] = value.ToArray();
            return Task.CompletedTask;
        }

        public Task<SecretValue?> RetrieveAsync(SecretName name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                _values.TryGetValue(name, out var value) ? new SecretValue(value) : null);
        }

        public Task DeleteAsync(SecretName name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.Remove(name);
            return Task.CompletedTask;
        }
    }
}
