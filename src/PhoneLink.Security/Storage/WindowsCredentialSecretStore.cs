using System.Security.Cryptography;
using System.Text;
using PhoneLink.Application.Ports;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;
using Windows.Storage;

namespace PhoneLink.Security.Storage;

/// <summary>
/// Stores device-bound application secrets as DPAPI-protected blobs. The historical
/// type name is retained so development installs can migrate Credential Locker values.
/// </summary>
public sealed class WindowsCredentialSecretStore : ISecretStore, IDisposable
{
    private const string LegacyResourceName = "PhoneLink.ProtectedSecret.v1";
    private readonly string _secretDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public WindowsCredentialSecretStore()
        : this(Path.Combine(ApplicationData.Current.LocalFolder.Path, "ProtectedSecrets"))
    {
    }

    internal WindowsCredentialSecretStore(string secretDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretDirectory);
        _secretDirectory = Path.GetFullPath(secretDirectory);
        Directory.CreateDirectory(_secretDirectory);
    }

    public async Task StoreAsync(
        SecretName name,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (value.IsEmpty)
        {
            throw new ArgumentException("A protected secret cannot be empty.", nameof(value));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ProtectAndWriteAsync(name, value, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SecretValue?> RetrieveAsync(
        SecretName name,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetPath(name);
            if (!File.Exists(path))
            {
                var legacy = TryRetrieveLegacy(name);
                if (legacy is null)
                {
                    return null;
                }

                using (legacy)
                {
                    await ProtectAndWriteAsync(name, legacy.Bytes, cancellationToken).ConfigureAwait(false);
                    RemoveLegacy(name);
                    return new SecretValue(legacy.Bytes.Span);
                }
            }

            var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            try
            {
                var provider = new DataProtectionProvider();
                var unprotected = await provider
                    .UnprotectAsync(CryptographicBuffer.CreateFromByteArray(protectedBytes))
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);
                CryptographicBuffer.CopyToByteArray(unprotected, out var plaintext);
                try
                {
                    return new SecretValue(plaintext);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(SecretName name, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetPath(name);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            RemoveLegacy(name);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _gate.Dispose();
    }

    private async Task ProtectAndWriteAsync(
        SecretName name,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken)
    {
        var plaintext = value.ToArray();
        byte[]? protectedBytes = null;
        try
        {
            var provider = new DataProtectionProvider("LOCAL=user");
            var protectedBuffer = await provider
                .ProtectAsync(CryptographicBuffer.CreateFromByteArray(plaintext))
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            CryptographicBuffer.CopyToByteArray(protectedBuffer, out protectedBytes);

            var destination = GetPath(name);
            var temporary = Path.Combine(_secretDirectory, $".{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4_096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(protectedBytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
    }

    private string GetPath(SecretName name)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(name.Value));
        return Path.Combine(_secretDirectory, $"{Convert.ToHexStringLower(digest)}.bin");
    }

    private static SecretValue? TryRetrieveLegacy(SecretName name)
    {
        try
        {
            var vault = new PasswordVault();
            var credential = vault.Retrieve(LegacyResourceName, name.Value);
            credential.RetrievePassword();
            var value = Convert.FromBase64String(credential.Password);
            try
            {
                return new SecretValue(value);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(value);
            }
        }
        catch (Exception exception) when (exception.HResult == unchecked((int)0x80070490))
        {
            return null;
        }
    }

    private static void RemoveLegacy(SecretName name)
    {
        try
        {
            var vault = new PasswordVault();
            vault.Remove(vault.Retrieve(LegacyResourceName, name.Value));
        }
        catch (Exception exception) when (exception.HResult == unchecked((int)0x80070490))
        {
        }
    }
}
