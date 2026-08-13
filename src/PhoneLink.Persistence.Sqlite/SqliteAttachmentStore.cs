using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using PhoneLink.Application.Features.Attachments;
using PhoneLink.Application.Ports;
using PhoneLink.Domain.Devices;

namespace PhoneLink.Persistence.Sqlite;

public sealed class SqliteAttachmentStore : IAttachmentStore
{
    private readonly SqliteDatabase _database;

    public SqliteAttachmentStore(SqliteDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task SaveVerifiedAsync(
        ImportedAttachment attachment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO attachments(
                attachment_id, owner_device_id, message_id, storage_name, display_name,
                media_type, byte_count, content_sha256, wrapped_file_key, encryption_nonce,
                transfer_state, created_at_utc, verified_at_utc, deleted_at_utc)
            VALUES(
                $attachmentId, $ownerDeviceId, NULL, $storageName, $displayName,
                $mediaType, $byteCount, $contentSha256, $protectedKeyReference, $nonce,
                'verified', $createdAt, $verifiedAt, NULL)
            ON CONFLICT(attachment_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$attachmentId", attachment.AttachmentId.ToString("D"));
        command.Parameters.AddWithValue("$ownerDeviceId", attachment.OwnerDeviceId.ToString());
        command.Parameters.AddWithValue("$storageName", attachment.StorageName);
        command.Parameters.AddWithValue("$displayName", attachment.DisplayName);
        command.Parameters.AddWithValue("$mediaType", attachment.MediaType);
        command.Parameters.AddWithValue("$byteCount", attachment.ByteCount);
        command.Parameters.AddWithValue("$contentSha256", attachment.ContentSha256);
        command.Parameters.AddWithValue(
            "$protectedKeyReference",
            Encoding.UTF8.GetBytes(attachment.ProtectedKeyReference));
        command.Parameters.AddWithValue("$nonce", attachment.EncryptionNonce);
        command.Parameters.AddWithValue("$createdAt", FormatTimestamp(attachment.CreatedAt));
        command.Parameters.AddWithValue("$verifiedAt", FormatTimestamp(attachment.VerifiedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ImportedAttachment>> ListAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 2_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT attachment_id, owner_device_id, storage_name, display_name, media_type,
                   byte_count, content_sha256, wrapped_file_key, encryption_nonce,
                   created_at_utc, verified_at_utc
            FROM attachments
            WHERE transfer_state = 'verified'
            ORDER BY verified_at_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        var results = new List<ImportedAttachment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new ImportedAttachment(
                Guid.Parse(reader.GetString(0)),
                new DeviceId(Guid.Parse(reader.GetString(1))),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                (byte[])reader[6],
                Encoding.UTF8.GetString((byte[])reader[7]),
                (byte[])reader[8],
                DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture)));
        }
        return results;
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
