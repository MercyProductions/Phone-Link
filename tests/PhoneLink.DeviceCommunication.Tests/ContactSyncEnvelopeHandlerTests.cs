using System.Text.Json;
using PhoneLink.Application.Features.Contacts;
using PhoneLink.Application.Features.Synchronization;
using PhoneLink.Application.Ports;
using PhoneLink.DeviceCommunication.Sessions;
using PhoneLink.DeviceCommunication.Synchronization;
using PhoneLink.Domain.Devices;
using PhoneLink.Protocol;
using PhoneLink.Protocol.Features.Contacts;
using PhoneLink.Protocol.Messages;
using PhoneLink.Protocol.Synchronization;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class ContactSyncEnvelopeHandlerTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AppliesUpsertAndReturnsContiguousAcknowledgement()
    {
        var store = new CapturingContactStore();
        var receipts = new InMemoryReceiptStore();
        var handler = new ContactSyncEnvelopeHandler(store, receipts);
        var deviceId = DeviceId.New();
        var batchId = Guid.NewGuid();
        var payload = new ContactPayload
        {
            SourceContactId = "ios-42",
            DisplayName = "Grace Hopper",
            GivenName = "Grace",
            FamilyName = "Hopper",
            OrganizationName = "US Navy",
            PhoneNumbers =
            [
                new ContactMethodPayload
                {
                    Label = "mobile",
                    DisplayValue = "+1 555 0100",
                    NormalizedValue = "+15550100",
                },
            ],
            EmailAddresses = [],
        };
        var batch = new SyncBatchPayload
        {
            BatchId = batchId,
            Events =
            [
                new SyncEventMessage
                {
                    EventId = Guid.NewGuid(),
                    OriginSequence = 1,
                    OccurredAt = DateTimeOffset.UtcNow,
                    EventType = "contact.upsert",
                    EntityId = payload.SourceContactId,
                    Payload = JsonSerializer.SerializeToElement(payload, WebJson),
                },
            ],
        };
        var envelope = new ProtocolEnvelope
        {
            ProtocolVersion = ProtocolConstants.CurrentVersion,
            MessageId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            OriginDeviceId = deviceId.Value,
            OriginSequence = 1,
            SentAt = DateTimeOffset.UtcNow,
            Kind = "sync.batch",
            RequiresAck = true,
            Payload = JsonSerializer.SerializeToElement(batch, WebJson),
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => handler.HandleAsync(
            new ReceivedDeviceEnvelope(deviceId, envelope with { OriginSequence = 2 }), CancellationToken.None));
        Assert.Empty(store.Upserts);

        var acknowledgement = await handler.HandleAsync(
            new ReceivedDeviceEnvelope(deviceId, envelope),
            CancellationToken.None);

        Assert.NotNull(acknowledgement);
        Assert.Equal(batchId, acknowledgement.BatchId);
        Assert.Equal(1, acknowledgement.HighestContiguousSequence);
        var contact = Assert.Single(store.Upserts);
        Assert.Equal("Grace Hopper", contact.DisplayName);
        Assert.Equal("+15550100", Assert.Single(contact.PhoneNumbers).NormalizedValue);

        var retry = await handler.HandleAsync(
            new ReceivedDeviceEnvelope(deviceId, envelope),
            CancellationToken.None);
        Assert.Equal(1, retry!.HighestContiguousSequence);
        Assert.Single(store.Upserts);

        var checkpoint = await handler.GetCheckpointAsync(
            Control(deviceId, "sync.checkpoint.request", new { }), CancellationToken.None);
        Assert.Equal(deviceId.Value, checkpoint.OriginDeviceId);
        Assert.Equal(1, checkpoint.HighestContiguousSequence);
        var otherDevice = DeviceId.New();
        var otherCheckpoint = await handler.GetCheckpointAsync(
            Control(otherDevice, "sync.checkpoint.request", new { }), CancellationToken.None);
        Assert.Equal(otherDevice.Value, otherCheckpoint.OriginDeviceId);
        Assert.Equal(0, otherCheckpoint.HighestContiguousSequence);
    }

    [Fact]
    public async Task ReconcilesOnlyTheAuthenticatedOwnersAuthorizedSubset()
    {
        var store = new CapturingContactStore();
        var handler = new ContactSyncEnvelopeHandler(store, new InMemoryReceiptStore());
        var owner = DeviceId.New();
        await handler.ReconcileAuthorizationAsync(Control(owner, "contacts.reconcile",
            new ContactReconciliationPayload { AuthorizedSourceContactIds = ["allowed-1", "allowed-2"] }),
            CancellationToken.None);
        var result = Assert.Single(store.Reconciliations);
        Assert.Equal(owner, result.Owner);
        Assert.Equal(["allowed-1", "allowed-2"], result.Identifiers);
    }

    [Theory]
    [InlineData("no-ack")]
    [InlineData("correlated")]
    [InlineData("durable-sequence")]
    [InlineData("spoofed-origin")]
    [InlineData("wrong-kind")]
    [InlineData("nonempty-payload")]
    public async Task RejectsMalformedCheckpointControls(string mutation)
    {
        var owner = DeviceId.New();
        var request = Control(owner, "sync.checkpoint.request", new { });
        var envelope = request.Envelope;
        envelope = mutation switch
        {
            "no-ack" => envelope with { RequiresAck = false },
            "correlated" => envelope with { CorrelationId = Guid.NewGuid() },
            "durable-sequence" => envelope with { OriginSequence = 1 },
            "spoofed-origin" => envelope with { OriginDeviceId = Guid.NewGuid() },
            "wrong-kind" => envelope with { Kind = "contacts.reconcile" },
            _ => envelope with { Payload = JsonSerializer.SerializeToElement(new { originDeviceId = Guid.NewGuid() }, WebJson) },
        };
        var handler = new ContactSyncEnvelopeHandler(new CapturingContactStore(), new InMemoryReceiptStore());
        await Assert.ThrowsAsync<InvalidDataException>(() => handler.GetCheckpointAsync(
            new ReceivedDeviceEnvelope(owner, envelope), CancellationToken.None));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("empty-id")]
    [InlineData("long-id")]
    [InlineData("too-many")]
    public async Task RejectsInvalidAuthorizationSnapshotsBeforeChangingContacts(string mutation)
    {
        var owner = DeviceId.New();
        var identifiers = mutation switch
        {
            "empty-id" => new[] { " " },
            "long-id" => new[] { new string('a', 501) },
            "too-many" => Enumerable.Repeat("a", 20_001).ToArray(),
            _ => null,
        };
        var store = new CapturingContactStore();
        var handler = new ContactSyncEnvelopeHandler(store, new InMemoryReceiptStore());
        await Assert.ThrowsAsync<InvalidDataException>(() => handler.ReconcileAuthorizationAsync(
            Control(owner, "contacts.reconcile", new { authorizedSourceContactIds = identifiers }), CancellationToken.None));
        Assert.Empty(store.Reconciliations);
    }

    private static ReceivedDeviceEnvelope Control<T>(DeviceId owner, string kind, T payload) => new(owner,
        new ProtocolEnvelope
        {
            ProtocolVersion = 1, MessageId = Guid.NewGuid(), SessionId = Guid.NewGuid(), OriginDeviceId = owner.Value,
            OriginSequence = 0, SentAt = DateTimeOffset.UtcNow, Kind = kind, RequiresAck = true,
            Payload = JsonSerializer.SerializeToElement(payload, WebJson),
        });

    private sealed class CapturingContactStore : IContactStore
    {
        public List<SyncedContact> Upserts { get; } = [];
        public List<(DeviceId Owner, string[] Identifiers)> Reconciliations { get; } = [];

        public Task ReconcileAuthorizationAsync(DeviceId ownerDeviceId,
            IReadOnlyList<string> authorizedSourceContactIds, DateTimeOffset observedAt,
            CancellationToken cancellationToken)
        {
            Reconciliations.Add((ownerDeviceId, authorizedSourceContactIds.ToArray()));
            return Task.CompletedTask;
        }

        public Task UpsertAsync(SyncedContact contact, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Upserts.Add(contact);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            DeviceId ownerDeviceId,
            string sourceContactId,
            DateTimeOffset deletedAt,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<SyncedContact>> ListAsync(
            DeviceId? ownerDeviceId,
            string? search,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SyncedContact>>(Upserts);
    }

    private sealed class InMemoryReceiptStore : IInboundSyncReceiptStore
    {
        private readonly Dictionary<Guid, InboundSyncReceipt> _receipts = [];

        public Task<long> GetHighestAppliedSequenceAsync(
            DeviceId originDeviceId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                _receipts.Values
                    .Where(receipt => receipt.OriginDeviceId == originDeviceId)
                    .Select(receipt => receipt.OriginSequence)
                    .DefaultIfEmpty()
                    .Max());

        public Task<SyncReceiptMatch> MatchAsync(
            InboundSyncReceipt receipt,
            CancellationToken cancellationToken)
        {
            var collision = _receipts.Values.FirstOrDefault(existing =>
                existing.EventId == receipt.EventId ||
                existing.OriginDeviceId == receipt.OriginDeviceId &&
                existing.OriginSequence == receipt.OriginSequence);
            var result = collision is null
                ? SyncReceiptMatch.Missing
                : collision.EventId == receipt.EventId &&
                  collision.OriginDeviceId == receipt.OriginDeviceId &&
                  collision.OriginSequence == receipt.OriginSequence &&
                  collision.EventSha256.Span.SequenceEqual(receipt.EventSha256.Span)
                    ? SyncReceiptMatch.ExactApplied
                    : SyncReceiptMatch.Conflict;
            return Task.FromResult(result);
        }

        public Task RecordAppliedAsync(
            InboundSyncReceipt receipt,
            CancellationToken cancellationToken)
        {
            _receipts.Add(receipt.EventId, receipt);
            return Task.CompletedTask;
        }
    }
}
