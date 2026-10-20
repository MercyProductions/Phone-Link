using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PhoneLink.Application.Features.Contacts;
using PhoneLink.Application.Ports;
using PhoneLink.Domain.Devices;

namespace PhoneLink.Persistence.Sqlite;

public sealed class SqliteContactStore : IContactStore
{
    private readonly SqliteDatabase _database;

    public SqliteContactStore(SqliteDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task UpsertAsync(SyncedContact contact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contact);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO contacts(
                        contact_id, owner_device_id, source_contact_id, source_kind,
                        source_revision, display_name, given_name, family_name,
                        organization_name, is_deleted, source_modified_at_utc,
                        created_at_utc, updated_at_utc)
                    VALUES(
                        $contactId, $ownerDeviceId, $sourceContactId, 'companion-contacts',
                        0, $displayName, $givenName, $familyName,
                        $organizationName, 0, $updatedAt, $updatedAt, $updatedAt)
                    ON CONFLICT(owner_device_id, source_kind, source_contact_id) DO UPDATE SET
                        display_name = excluded.display_name,
                        given_name = excluded.given_name,
                        family_name = excluded.family_name,
                        organization_name = excluded.organization_name,
                        is_deleted = 0,
                        source_modified_at_utc = excluded.source_modified_at_utc,
                        updated_at_utc = excluded.updated_at_utc;
                    """;
                AddContactParameters(command, contact);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var deleteMethods = connection.CreateCommand())
            {
                deleteMethods.Transaction = (SqliteTransaction)transaction;
                deleteMethods.CommandText = "DELETE FROM contact_methods WHERE contact_id = $contactId;";
                deleteMethods.Parameters.AddWithValue("$contactId", contact.ContactId);
                await deleteMethods.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await InsertMethodsAsync(
                connection,
                (SqliteTransaction)transaction,
                contact.ContactId,
                "phone",
                contact.PhoneNumbers,
                cancellationToken).ConfigureAwait(false);
            await InsertMethodsAsync(
                connection,
                (SqliteTransaction)transaction,
                contact.ContactId,
                "email",
                contact.EmailAddresses,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task DeleteAsync(
        DeviceId ownerDeviceId,
        string sourceContactId,
        DateTimeOffset deletedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceContactId);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE contacts
            SET is_deleted = 1, updated_at_utc = $deletedAt
            WHERE owner_device_id = $ownerDeviceId
              AND source_kind = 'companion-contacts'
              AND source_contact_id = $sourceContactId;
            """;
        command.Parameters.AddWithValue("$ownerDeviceId", ownerDeviceId.ToString());
        command.Parameters.AddWithValue("$sourceContactId", sourceContactId);
        command.Parameters.AddWithValue("$deletedAt", FormatTimestamp(deletedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReconcileAuthorizationAsync(
        DeviceId ownerDeviceId,
        IReadOnlyList<string> authorizedSourceContactIds,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorizedSourceContactIds);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            DELETE FROM contact_methods WHERE contact_id IN (
                SELECT contact_id FROM contacts
                WHERE owner_device_id = $owner AND source_kind = 'companion-contacts'
                AND source_contact_id NOT IN (SELECT value FROM json_each($authorized))
            );
            UPDATE contacts SET is_deleted = 1, display_name = '', given_name = '',
                family_name = '', organization_name = '', updated_at_utc = $observedAt
            WHERE owner_device_id = $owner AND source_kind = 'companion-contacts'
                AND source_contact_id NOT IN (SELECT value FROM json_each($authorized));
            UPDATE sync_events SET payload_json = '{}'
            WHERE origin_device_id = $owner AND event_type IN ('contact.upsert', 'contact.delete')
                AND entity_id NOT IN (SELECT value FROM json_each($authorized));
            """;
        command.Parameters.AddWithValue("$owner", ownerDeviceId.ToString());
        command.Parameters.AddWithValue("$authorized", JsonSerializer.Serialize(authorizedSourceContactIds));
        command.Parameters.AddWithValue("$observedAt", FormatTimestamp(observedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SyncedContact>> ListAsync(
        DeviceId? ownerDeviceId,
        string? search,
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
            SELECT contact_id, owner_device_id, source_contact_id, display_name,
                   given_name, family_name, organization_name, updated_at_utc
            FROM contacts
            WHERE is_deleted = 0
              AND ($ownerDeviceId IS NULL OR owner_device_id = $ownerDeviceId)
              AND ($search = '' OR display_name LIKE '%' || $search || '%' COLLATE NOCASE)
            ORDER BY display_name COLLATE NOCASE
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$ownerDeviceId", ownerDeviceId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$search", search?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$limit", limit);

        var rows = new List<ContactRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new ContactRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7)));
            }
        }

        var contacts = new List<SyncedContact>(rows.Count);
        foreach (var row in rows)
        {
            contacts.Add(new SyncedContact(
                row.ContactId,
                new DeviceId(Guid.Parse(row.OwnerDeviceId)),
                row.SourceContactId,
                row.DisplayName,
                row.GivenName,
                row.FamilyName,
                row.OrganizationName,
                await ReadMethodsAsync(connection, row.ContactId, "phone", cancellationToken).ConfigureAwait(false),
                await ReadMethodsAsync(connection, row.ContactId, "email", cancellationToken).ConfigureAwait(false),
                false,
                DateTimeOffset.Parse(row.UpdatedAt, CultureInfo.InvariantCulture)));
        }
        return contacts;
    }

    private static void AddContactParameters(SqliteCommand command, SyncedContact contact)
    {
        command.Parameters.AddWithValue("$contactId", contact.ContactId);
        command.Parameters.AddWithValue("$ownerDeviceId", contact.OwnerDeviceId.ToString());
        command.Parameters.AddWithValue("$sourceContactId", contact.SourceContactId);
        command.Parameters.AddWithValue("$displayName", contact.DisplayName);
        command.Parameters.AddWithValue("$givenName", contact.GivenName);
        command.Parameters.AddWithValue("$familyName", contact.FamilyName);
        command.Parameters.AddWithValue("$organizationName", contact.OrganizationName);
        command.Parameters.AddWithValue("$updatedAt", FormatTimestamp(contact.UpdatedAt));
    }

    private static async Task InsertMethodsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string contactId,
        string methodKind,
        IReadOnlyList<ContactMethod> methods,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < methods.Count; index++)
        {
            var method = methods[index];
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO contact_methods(
                    contact_method_id, contact_id, method_kind, label,
                    display_value, normalized_value, sort_order, is_primary)
                VALUES($id, $contactId, $kind, $label, $display, $normalized, $sortOrder, $isPrimary);
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$contactId", contactId);
            command.Parameters.AddWithValue("$kind", methodKind);
            command.Parameters.AddWithValue("$label", method.Label);
            command.Parameters.AddWithValue("$display", method.DisplayValue);
            command.Parameters.AddWithValue("$normalized", method.NormalizedValue);
            command.Parameters.AddWithValue("$sortOrder", index);
            command.Parameters.AddWithValue("$isPrimary", index == 0 ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<ContactMethod>> ReadMethodsAsync(
        SqliteConnection connection,
        string contactId,
        string methodKind,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT label, display_value, normalized_value
            FROM contact_methods
            WHERE contact_id = $contactId AND method_kind = $kind
            ORDER BY sort_order;
            """;
        command.Parameters.AddWithValue("$contactId", contactId);
        command.Parameters.AddWithValue("$kind", methodKind);
        var methods = new List<ContactMethod>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            methods.Add(new ContactMethod(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        return methods;
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed record ContactRow(
        string ContactId,
        string OwnerDeviceId,
        string SourceContactId,
        string DisplayName,
        string GivenName,
        string FamilyName,
        string OrganizationName,
        string UpdatedAt);
}
