using PhoneLink.Application.Ports;

namespace PhoneLink.DeviceCommunication.Attachments;

public sealed record AttachmentStagingRecoveryReport(int Removed, int Skipped, int Unavailable, bool LimitReached);

internal static class AttachmentStagingRecovery
{
    private const string Suffix = ".incoming-v3";
    private const int MaximumCandidates = 64;
    private static readonly TimeSpan Grace = TimeSpan.FromHours(24);

    internal static string StagingName(Guid attachmentId, Guid keyId) => $".{attachmentId:N}.{keyId:N}{Suffix}";
    internal static SecretName KeyName(Guid attachmentId, Guid keyId) => new($"attachment/staged-file-key-v3/{attachmentId:N}/{keyId:N}");

    internal static async Task<AttachmentStagingRecoveryReport> RunAsync(string directory, ISecretStore secrets,
        DateTimeOffset now, CancellationToken token)
    {
        var removed = 0;
        var skipped = 0;
        var unavailable = 0;
        var seen = 0;
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return new(0, 0, 1, false);
            foreach (var path in Directory.EnumerateFiles(directory, "*.incoming-v3", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                if (++seen > MaximumCandidates) return new(removed, skipped, unavailable, true);
                if (!TryParse(Path.GetFileName(path), out var attachmentId, out var keyId)) { skipped++; continue; }
                var completed = Path.Combine(directory, $"{attachmentId:N}.pla");
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                        File.GetLastWriteTimeUtc(path) > now.UtcDateTime - Grace || File.Exists(completed) || Directory.Exists(completed))
                    { skipped++; continue; }
                    // A live writer denies reads. Keep this read handle until removal so no writer
                    // can open the candidate between the second eligibility check and deletion.
                    using var claim = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete);
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                        File.GetLastWriteTimeUtc(path) > now.UtcDateTime - Grace || File.Exists(completed) || Directory.Exists(completed))
                    { skipped++; continue; }
                    await secrets.DeleteAsync(KeyName(attachmentId, keyId), token).ConfigureAwait(false);
                    File.Delete(path);
                    removed++;
                }
                catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException)) { unavailable++; }
            }
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException)) { unavailable++; }
        return new(removed, skipped, unavailable, false);
    }

    private static bool TryParse(string name, out Guid attachmentId, out Guid keyId)
    {
        attachmentId = keyId = Guid.Empty;
        return name.Length == 66 + Suffix.Length && name[0] == '.' && name[33] == '.' &&
            name.EndsWith(Suffix, StringComparison.Ordinal) && Guid.TryParseExact(name.AsSpan(1, 32), "N", out attachmentId) &&
            Guid.TryParseExact(name.AsSpan(34, 32), "N", out keyId) && attachmentId != Guid.Empty && keyId != Guid.Empty &&
            string.Equals(name, StagingName(attachmentId, keyId), StringComparison.Ordinal);
    }
}
