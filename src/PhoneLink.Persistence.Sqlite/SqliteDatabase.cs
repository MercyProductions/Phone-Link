using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PhoneLink.Application.Ports;
using PhoneLink.Persistence.Sqlite.Configuration;

namespace PhoneLink.Persistence.Sqlite;

public sealed class SqliteDatabase : IDisposable
{
    private static readonly SecretName DatabaseKeyName = new("database/master-key-v1");
    private const string InitialMigrationResource =
        "PhoneLink.Persistence.Sqlite.Migrations.001_initial.sql";

    private readonly SqliteStorageOptions _options;
    private readonly ISecretStore _secretStore;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    static SqliteDatabase()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    public SqliteDatabase(SqliteStorageOptions options, ISecretStore secretStore)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_options.DatabasePath)!);
            Directory.CreateDirectory(_options.AttachmentDirectory);

            await using var connection = await CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

            var currentVersion = await GetUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            if (currentVersion == 0)
            {
                await ApplyInitialMigrationAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            else if (currentVersion != SqliteStorageOptions.CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Database schema version {currentVersion} is not supported by this build.");
            }

            await VerifyInitialMigrationAsync(connection, cancellationToken).ConfigureAwait(false);
            await VerifyIntegrityAsync(connection, cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var connection = await CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _initializationLock.Dispose();
        SqliteConnection.ClearAllPools();
        _disposed = true;
    }

    private async Task<SqliteConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        using var key = await LoadOrCreateDatabaseKeyAsync(cancellationToken).ConfigureAwait(false);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            Password = Convert.ToBase64String(key.Bytes.Span),
        };
        return new SqliteConnection(builder.ConnectionString);
    }

    private async Task<SecretValue> LoadOrCreateDatabaseKeyAsync(CancellationToken cancellationToken)
    {
        var existing = await _secretStore
            .RetrieveAsync(DatabaseKeyName, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Bytes.Length != 32)
            {
                existing.Dispose();
                throw new InvalidOperationException("The protected database key has an invalid length.");
            }

            return existing;
        }

        var newKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            await _secretStore
                .StoreAsync(DatabaseKeyName, newKey, cancellationToken)
                .ConfigureAwait(false);
            return new SecretValue(newKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(newKey);
        }
    }

    private static async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            PRAGMA secure_delete = ON;
            PRAGMA trusted_schema = OFF;
            PRAGMA temp_store = MEMORY;
            PRAGMA busy_timeout = 5000;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> GetUserVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task ApplyInitialMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var migration = ReadInitialMigration();
        var checksum = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(migration));

        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using (var schemaCommand = connection.CreateCommand())
            {
                schemaCommand.Transaction = (SqliteTransaction)transaction;
                schemaCommand.CommandText = migration;
                await schemaCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var historyCommand = connection.CreateCommand())
            {
                historyCommand.Transaction = (SqliteTransaction)transaction;
                historyCommand.CommandText = """
                    INSERT INTO schema_migrations(version, name, checksum_sha256, applied_at_utc)
                    VALUES (1, 'initial', $checksum, $appliedAt);
                    PRAGMA user_version = 1;
                    """;
                historyCommand.Parameters.AddWithValue("$checksum", checksum);
                historyCommand.Parameters.AddWithValue("$appliedAt", FormatTimestamp(DateTimeOffset.UtcNow));
                await historyCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task VerifyInitialMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var migration = ReadInitialMigration();
        var expected = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(migration));

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT checksum_sha256 FROM schema_migrations WHERE version = 1;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is not byte[] actual ||
            actual.Length != expected.Length ||
            !CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new InvalidOperationException("The initial database migration checksum does not match this build.");
        }
    }

    private static async Task VerifyIntegrityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var result = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (!string.Equals(result, "ok", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Phone Link database did not pass SQLite quick_check.");
        }
    }

    private static string ReadInitialMigration()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(InitialMigrationResource)
            ?? throw new InvalidOperationException("The initial database migration is missing from this build.");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
