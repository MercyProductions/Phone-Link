using PhoneLink.Persistence.Sqlite.Configuration;

namespace PhoneLink.Persistence.Sqlite.Tests;

public sealed class SqliteStorageOptionsTests
{
    [Fact]
    public void AcceptsDedicatedAbsoluteStoragePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "PhoneLinkTests");
        var databasePath = Path.Combine(root, "database", "phonelink.db");
        var attachmentDirectory = Path.Combine(root, "attachments");

        var options = new SqliteStorageOptions(databasePath, attachmentDirectory);

        Assert.Equal(Path.GetFullPath(databasePath), options.DatabasePath);
        Assert.Equal(Path.GetFullPath(attachmentDirectory), options.AttachmentDirectory);
    }

    [Fact]
    public void RejectsNonDatabaseExtension()
    {
        var root = Path.Combine(Path.GetTempPath(), "PhoneLinkTests");

        Assert.Throws<ArgumentException>(() => new SqliteStorageOptions(
            Path.Combine(root, "phonelink.txt"),
            Path.Combine(root, "attachments")));
    }
}

