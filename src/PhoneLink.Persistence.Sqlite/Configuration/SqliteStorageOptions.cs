namespace PhoneLink.Persistence.Sqlite.Configuration;

public sealed record SqliteStorageOptions
{
    public const int CurrentSchemaVersion = 1;

    public SqliteStorageOptions(string databasePath, string attachmentDirectory)
    {
        DatabasePath = ValidateAbsolutePath(databasePath, nameof(databasePath));
        AttachmentDirectory = ValidateAbsolutePath(attachmentDirectory, nameof(attachmentDirectory));

        if (!string.Equals(Path.GetExtension(DatabasePath), ".db", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The database path must use a .db extension.", nameof(databasePath));
        }

        var databaseDirectory = Path.GetDirectoryName(DatabasePath)
            ?? throw new ArgumentException("The database must have a parent directory.", nameof(databasePath));

        if (string.Equals(DatabasePath, AttachmentDirectory, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(databaseDirectory, AttachmentDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Attachments must use a dedicated child directory, not the database path or its parent directory.",
                nameof(attachmentDirectory));
        }
    }

    public string DatabasePath { get; }

    public string AttachmentDirectory { get; }

    private static string ValidateAbsolutePath(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        var fullPath = Path.GetFullPath(value);
        if (!Path.IsPathFullyQualified(fullPath))
        {
            throw new ArgumentException("The storage path must be fully qualified.", parameterName);
        }

        return fullPath;
    }
}

