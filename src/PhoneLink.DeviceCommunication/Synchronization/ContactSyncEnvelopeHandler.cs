using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using PhoneLink.Application.Features.Contacts;
using PhoneLink.Application.Features.Synchronization;
using PhoneLink.Application.Ports;
using PhoneLink.DeviceCommunication.Sessions;
using PhoneLink.Domain.Devices;
using PhoneLink.Protocol.Features.Contacts;
using PhoneLink.Protocol.Synchronization;

namespace PhoneLink.DeviceCommunication.Synchronization;

public sealed class ContactSyncEnvelopeHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly IContactStore _contactStore;
    private readonly IInboundSyncReceiptStore _receiptStore;

    public ContactSyncEnvelopeHandler(
        IContactStore contactStore,
        IInboundSyncReceiptStore receiptStore)
    {
        _contactStore = contactStore ?? throw new ArgumentNullException(nameof(contactStore));
        _receiptStore = receiptStore ?? throw new ArgumentNullException(nameof(receiptStore));
    }

    public async Task<SyncAckPayload?> HandleAsync(
        ReceivedDeviceEnvelope received,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(received);
        if (received.Envelope.Kind != "sync.batch")
        {
            return null;
        }

        var batch = received.Envelope.Payload.Deserialize<SyncBatchPayload>(JsonOptions)
            ?? throw new InvalidDataException("A sync batch payload cannot be null.");
        if (batch.BatchId == Guid.Empty || batch.Events is not { Length: > 0 and <= 200 })
        {
            throw new InvalidDataException("The sync batch metadata is invalid.");
        }

        var highestSequence = await _receiptStore
            .GetHighestAppliedSequenceAsync(received.DeviceId, cancellationToken)
            .ConfigureAwait(false);
        var previousBatchSequence = 0L;
        foreach (var syncEvent in batch.Events)
        {
            ValidateEvent(syncEvent, previousBatchSequence);
            previousBatchSequence = syncEvent.OriginSequence;
            var receipt = CreateReceipt(received.DeviceId, syncEvent);
            var match = await _receiptStore.MatchAsync(receipt, cancellationToken).ConfigureAwait(false);
            if (match == SyncReceiptMatch.ExactApplied)
            {
                if (syncEvent.OriginSequence > highestSequence)
                {
                    throw new InvalidDataException("A duplicate sync event was found beyond the applied checkpoint.");
                }
                continue;
            }
            if (match == SyncReceiptMatch.Conflict)
            {
                throw new InvalidDataException("A sync event ID or origin sequence was reused with different content.");
            }
            if (syncEvent.OriginSequence != highestSequence + 1)
            {
                throw new InvalidDataException(
                    $"The sync batch has a sequence gap. Expected {highestSequence + 1}.");
            }

            switch (syncEvent.EventType)
            {
                case "contact.upsert":
                    await UpsertContactAsync(received, syncEvent, cancellationToken).ConfigureAwait(false);
                    break;
                case "contact.delete":
                    await _contactStore.DeleteAsync(
                        received.DeviceId,
                        syncEvent.EntityId,
                        syncEvent.OccurredAt,
                        cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidDataException($"Unsupported sync event type '{syncEvent.EventType}'.");
            }
            await _receiptStore.RecordAppliedAsync(receipt, cancellationToken).ConfigureAwait(false);
            highestSequence = syncEvent.OriginSequence;
        }

        if (received.Envelope.OriginSequence != batch.Events[^1].OriginSequence)
        {
            throw new InvalidDataException("The batch envelope sequence must equal its final event sequence.");
        }

        return new SyncAckPayload
        {
            BatchId = batch.BatchId,
            OriginDeviceId = received.DeviceId.Value,
            HighestContiguousSequence = highestSequence,
        };
    }

    public async Task<SyncCheckpointPayload> GetCheckpointAsync(
        ReceivedDeviceEnvelope received,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(received);
        return new SyncCheckpointPayload
        {
            OriginDeviceId = received.DeviceId.Value,
            HighestContiguousSequence = await _receiptStore
                .GetHighestAppliedSequenceAsync(received.DeviceId, cancellationToken).ConfigureAwait(false),
        };
    }

    public async Task ReconcileAuthorizationAsync(
        ReceivedDeviceEnvelope received,
        CancellationToken cancellationToken)
    {
        var payload = received.Envelope.Payload.Deserialize<ContactReconciliationPayload>(JsonOptions)
            ?? throw new InvalidDataException("Contact authorization payload is required.");
        if (payload.AuthorizedSourceContactIds is not { Length: <= 20_000 } ||
            payload.AuthorizedSourceContactIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 500))
        {
            throw new InvalidDataException("The contact authorization snapshot exceeds protocol limits.");
        }
        await _contactStore.ReconcileAuthorizationAsync(received.DeviceId,
            payload.AuthorizedSourceContactIds, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
    }

    private static InboundSyncReceipt CreateReceipt(
        DeviceId originDeviceId,
        SyncEventMessage syncEvent)
    {
        var payloadJson = syncEvent.Payload.GetRawText();
        var hashInput = string.Join(
            '\n',
            syncEvent.EventId.ToString("D"),
            syncEvent.OriginSequence.ToString(CultureInfo.InvariantCulture),
            syncEvent.OccurredAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            syncEvent.EventType,
            syncEvent.EntityId,
            payloadJson);
        return new InboundSyncReceipt(
            syncEvent.EventId,
            originDeviceId,
            syncEvent.OriginSequence,
            syncEvent.EventType,
            syncEvent.EntityId,
            syncEvent.OccurredAt,
            payloadJson,
            SHA256.HashData(Encoding.UTF8.GetBytes(hashInput)),
            DateTimeOffset.UtcNow);
    }

    private async Task UpsertContactAsync(
        ReceivedDeviceEnvelope received,
        SyncEventMessage syncEvent,
        CancellationToken cancellationToken)
    {
        var payload = syncEvent.Payload.Deserialize<ContactPayload>(JsonOptions)
            ?? throw new InvalidDataException("A contact payload cannot be null.");
        ValidateContact(payload, syncEvent.EntityId);

        var contact = new SyncedContact(
            CreateContactId(received.DeviceId.ToString(), payload.SourceContactId),
            received.DeviceId,
            payload.SourceContactId,
            payload.DisplayName.Trim(),
            payload.GivenName.Trim(),
            payload.FamilyName.Trim(),
            payload.OrganizationName.Trim(),
            payload.PhoneNumbers.Select(ToContactMethod).ToArray(),
            payload.EmailAddresses.Select(ToContactMethod).ToArray(),
            false,
            syncEvent.OccurredAt);
        await _contactStore.UpsertAsync(contact, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateEvent(SyncEventMessage syncEvent, long previousSequence)
    {
        if (syncEvent.EventId == Guid.Empty ||
            syncEvent.OriginSequence <= previousSequence ||
            string.IsNullOrWhiteSpace(syncEvent.EntityId) ||
            syncEvent.EntityId.Length > 500 ||
            syncEvent.Payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("A sync event has invalid metadata or ordering.");
        }
    }

    private static void ValidateContact(ContactPayload contact, string entityId)
    {
        if (!string.Equals(contact.SourceContactId, entityId, StringComparison.Ordinal) ||
            contact.SourceContactId.Length > 500 ||
            contact.DisplayName.Length > 500 ||
            contact.GivenName.Length > 200 ||
            contact.FamilyName.Length > 200 ||
            contact.OrganizationName.Length > 300 ||
            contact.PhoneNumbers.Length > 50 ||
            contact.EmailAddresses.Length > 50 ||
            contact.PhoneNumbers.Any(IsInvalidMethod) ||
            contact.EmailAddresses.Any(IsInvalidMethod))
        {
            throw new InvalidDataException("A synchronized contact exceeds the protocol limits.");
        }
    }

    private static bool IsInvalidMethod(ContactMethodPayload method) =>
        method.Label.Length > 100 ||
        string.IsNullOrWhiteSpace(method.DisplayValue) ||
        method.DisplayValue.Length > 500 ||
        string.IsNullOrWhiteSpace(method.NormalizedValue) ||
        method.NormalizedValue.Length > 500;

    private static ContactMethod ToContactMethod(ContactMethodPayload method) =>
        new(method.Label.Trim(), method.DisplayValue.Trim(), method.NormalizedValue.Trim());

    private static string CreateContactId(string ownerDeviceId, string sourceContactId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{ownerDeviceId}\n{sourceContactId}"));
        return new Guid(hash.AsSpan(0, 16)).ToString("D");
    }
}
