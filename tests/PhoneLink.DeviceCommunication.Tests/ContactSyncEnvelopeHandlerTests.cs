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
            new ReceivedDeviceEnvelope(deviceId, envelope), CancellationToken.None);
        Assert.Equal(deviceId.Value, checkpoint.OriginDeviceId);
        Assert.Equal(1, checkpoint.HighestContiguousSequence);
        var otherDevice = DeviceId.New();
        var otherCheckpoint = await handler.GetCheckpointAsync(
            new ReceivedDeviceEnvelope(otherDevice, envelope), CancellationToken.None);
        Assert.Equal(otherDevice.Value, otherCheckpoint.OriginDeviceId);
        Assert.Equal(0, otherCheckpoint.HighestContiguousSequence);
    }

    private sealed class CapturingContactStore : IContactStore
    {
        public List<SyncedContact> Upserts { get; } = [];

        public Task ReconcileAuthorizationAsync(DeviceId ownerDeviceId,
            IReadOnlyList<string> authorizedSourceContactIds, DateTimeOffset observedAt,
            CancellationToken cancellationToken) => Task.CompletedTask;

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
