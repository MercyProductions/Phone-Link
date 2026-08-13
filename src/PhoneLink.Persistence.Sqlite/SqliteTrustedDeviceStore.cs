using System.Globalization;
using Microsoft.Data.Sqlite;
using PhoneLink.Application.Ports;
using PhoneLink.Domain.Devices;

namespace PhoneLink.Persistence.Sqlite;

public sealed class SqliteTrustedDeviceStore : ITrustedDeviceStore
{
    private const string Projection = """
        device_id, display_name, platform, identity_public_key,
        paired_at_utc, trust_state, revoked_at_utc
        """;

    private readonly SqliteDatabase _database;

    public SqliteTrustedDeviceStore(SqliteDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<TrustedDevice?> FindAsync(DeviceId id, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Projection} FROM devices WHERE device_id = $deviceId;";
        command.Parameters.AddWithValue("$deviceId", id.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadDevice(reader)
            : null;
    }

    public async Task<IReadOnlyList<TrustedDevice>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Projection} FROM devices ORDER BY paired_at_utc DESC;";

        var devices = new List<TrustedDevice>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            devices.Add(ReadDevice(reader));
        }

        return devices;
    }

    public async Task SaveAsync(TrustedDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO devices(
                device_id, display_name, platform, trust_state, identity_public_key,
                tls_spki_sha256, protocol_floor, protocol_ceiling, paired_at_utc,
                last_seen_at_utc, revoked_at_utc, capabilities_json, permissions_json,
                created_at_utc, updated_at_utc)
            VALUES(
                $deviceId, $displayName, $platform, $trustState, $identityPublicKey,
                NULL, 1, 1, $pairedAt, NULL, $revokedAt, '[]', '{}', $createdAt, $updatedAt)
            ON CONFLICT(device_id) DO UPDATE SET
                display_name = excluded.display_name,
                platform = excluded.platform,
                trust_state = excluded.trust_state,
                identity_public_key = excluded.identity_public_key,
                paired_at_utc = excluded.paired_at_utc,
                revoked_at_utc = excluded.revoked_at_utc,
                updated_at_utc = excluded.updated_at_utc;
            """;

        var now = DateTimeOffset.UtcNow;
        command.Parameters.AddWithValue("$deviceId", device.Id.ToString());
        command.Parameters.AddWithValue("$displayName", device.DisplayName);
        command.Parameters.AddWithValue("$platform", ToDatabaseValue(device.Platform));
        command.Parameters.AddWithValue("$trustState", ToDatabaseValue(device.TrustState));
        command.Parameters.AddWithValue("$identityPublicKey", device.IdentityPublicKey.ToArray());
        command.Parameters.AddWithValue("$pairedAt", SqliteDatabase.FormatTimestamp(device.PairedAt));
        command.Parameters.AddWithValue(
            "$revokedAt",
            device.RevokedAt is { } revokedAt
                ? SqliteDatabase.FormatTimestamp(revokedAt)
                : DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", SqliteDatabase.FormatTimestamp(now));
        command.Parameters.AddWithValue("$updatedAt", SqliteDatabase.FormatTimestamp(now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RevokeAsync(
        DeviceId id,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE devices
            SET trust_state = 'revoked', revoked_at_utc = $revokedAt, updated_at_utc = $revokedAt
            WHERE device_id = $deviceId AND trust_state = 'trusted' AND paired_at_utc <= $revokedAt;
            """;
        command.Parameters.AddWithValue("$deviceId", id.ToString());
        command.Parameters.AddWithValue("$revokedAt", SqliteDatabase.FormatTimestamp(revokedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static TrustedDevice ReadDevice(SqliteDataReader reader)
    {
        var id = DeviceId.Parse(reader.GetString(0));
        var displayName = reader.GetString(1);
        var platform = reader.GetString(2) switch
        {
            "windows" => DevicePlatform.Windows,
            "ios" => DevicePlatform.Ios,
            var value => throw new InvalidDataException($"Unsupported device platform '{value}'."),
        };
        var identityPublicKey = (byte[])reader[3];
        var pairedAt = DateTimeOffset.Parse(
            reader.GetString(4),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        var device = TrustedDevice.Create(id, displayName, platform, identityPublicKey, pairedAt);

        return reader.GetString(5) switch
        {
            "trusted" => device,
            "revoked" => device.Revoke(
                DateTimeOffset.Parse(
                    reader.GetString(6),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind)),
            var value => throw new InvalidDataException($"Unsupported trust state '{value}'."),
        };
    }

    private static string ToDatabaseValue(DevicePlatform platform) => platform switch
    {
        DevicePlatform.Windows => "windows",
        DevicePlatform.Ios => "ios",
        _ => throw new ArgumentOutOfRangeException(nameof(platform)),
    };

    private static string ToDatabaseValue(DeviceTrustState state) => state switch
    {
        DeviceTrustState.Trusted => "trusted",
        DeviceTrustState.Revoked => "revoked",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}
