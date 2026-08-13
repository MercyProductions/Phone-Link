using Microsoft.Data.Sqlite;
using System.Globalization;
using PhoneLink.Application.Features.Synchronization;
using PhoneLink.Application.Ports;
using PhoneLink.Domain.Devices;

namespace PhoneLink.Persistence.Sqlite;

public sealed class SqliteInboundSyncReceiptStore : IInboundSyncReceiptStore
{
    private readonly SqliteDatabase _database;

    public SqliteInboundSyncReceiptStore(SqliteDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<long> GetHighestAppliedSequenceAsync(
        DeviceId originDeviceId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(MAX(origin_sequence), 0)
            FROM sync_events
            WHERE origin_device_id = $originDeviceId AND apply_status = 'applied';
            """;
        command.Parameters.AddWithValue("$originDeviceId", originDeviceId.ToString());
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    public async Task<SyncReceiptMatch> MatchAsync(
        InboundSyncReceipt receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, origin_device_id, origin_sequence, payload_sha256, apply_status
            FROM sync_events
            WHERE event_id = $eventId
               OR (origin_device_id = $originDeviceId AND origin_sequence = $originSequence)
            LIMIT 2;
            """;
        command.Parameters.AddWithValue("$eventId", receipt.EventId.ToString("D"));
        command.Parameters.AddWithValue("$originDeviceId", receipt.OriginDeviceId.ToString());
        command.Parameters.AddWithValue("$originSequence", receipt.OriginSequence);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = 0;
        var exact = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows++;
            exact = exact ||
                string.Equals(reader.GetString(0), receipt.EventId.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(reader.GetString(1), receipt.OriginDeviceId.ToString(), StringComparison.OrdinalIgnoreCase) &&
                reader.GetInt64(2) == receipt.OriginSequence &&
                reader.GetFieldValue<byte[]>(3).AsSpan().SequenceEqual(receipt.EventSha256.Span) &&
                string.Equals(reader.GetString(4), "applied", StringComparison.Ordinal);
        }
        return rows switch
        {
            0 => SyncReceiptMatch.Missing,
            1 when exact => SyncReceiptMatch.ExactApplied,
            _ => SyncReceiptMatch.Conflict,
        };
    }

    public async Task RecordAppliedAsync(
        InboundSyncReceipt receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_events(
                event_id, origin_device_id, origin_sequence, event_type, entity_id,
                occurred_at_utc, received_at_utc, payload_json, payload_sha256,
                apply_status, is_optional, applied_at_utc, prune_after_utc)
            VALUES(
                $eventId, $originDeviceId, $originSequence, $eventType, $entityId,
                $occurredAt, $receivedAt, $payloadJson, $payloadSha256,
                'applied', 0, $receivedAt, $pruneAfter);
            """;
        command.Parameters.AddWithValue("$eventId", receipt.EventId.ToString("D"));
        command.Parameters.AddWithValue("$originDeviceId", receipt.OriginDeviceId.ToString());
        command.Parameters.AddWithValue("$originSequence", receipt.OriginSequence);
        command.Parameters.AddWithValue("$eventType", receipt.EventType);
        command.Parameters.AddWithValue("$entityId", receipt.EntityId);
        command.Parameters.AddWithValue("$occurredAt", SqliteDatabase.FormatTimestamp(receipt.OccurredAt));
        command.Parameters.AddWithValue("$receivedAt", SqliteDatabase.FormatTimestamp(receipt.ReceivedAt));
        command.Parameters.AddWithValue("$payloadJson", receipt.PayloadJson);
        command.Parameters.AddWithValue("$payloadSha256", receipt.EventSha256.ToArray());
        command.Parameters.AddWithValue(
            "$pruneAfter",
            SqliteDatabase.FormatTimestamp(receipt.ReceivedAt.AddDays(7)));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
