using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhoneLink.Application.Features.Attachments;
using PhoneLink.Application.Ports;
using PhoneLink.DeviceCommunication.Sessions;
using PhoneLink.Protocol.Attachments;
using PhoneLink.Protocol.Pairing;

namespace PhoneLink.DeviceCommunication.Attachments;

public sealed class InboundAttachmentManager : IAsyncDisposable
{
    private const long MaximumAttachmentBytes = 25 * 1024 * 1024;
    private static readonly HashSet<string> AllowedMediaTypes = new(
        ["image/jpeg", "image/png", "image/heic", "image/heif", "image/webp"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly string _attachmentDirectory;
    private readonly ISecretStore _secretStore;
    private readonly IAttachmentStore _attachmentStore;
    private readonly ConcurrentDictionary<Guid, TransferState> _transfers = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private bool _disposed;

    public InboundAttachmentManager(
        string attachmentDirectory,
        ISecretStore secretStore,
        IAttachmentStore attachmentStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attachmentDirectory);
        _attachmentDirectory = Path.GetFullPath(attachmentDirectory);
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _attachmentStore = attachmentStore ?? throw new ArgumentNullException(nameof(attachmentStore));
        Directory.CreateDirectory(_attachmentDirectory);
    }

    public async Task<AttachmentControlResponse?> HandleEnvelopeAsync(
        ReceivedDeviceEnvelope received,
        CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await HandleEnvelopeCoreAsync(received, cancellationToken).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    private async Task<AttachmentControlResponse?> HandleEnvelopeCoreAsync(
        ReceivedDeviceEnvelope received, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(received);
        return received.Envelope.Kind switch
        {
            "attachment.offer" => await HandleOfferAsync(received, cancellationToken).ConfigureAwait(false),
            "attachment.complete" => await HandleCompleteAsync(received, cancellationToken).ConfigureAwait(false),
            _ => null,
        };
    }

    public async Task HandleChunkAsync(
        ReceivedAttachmentChunk received,
        CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await HandleChunkCoreAsync(received, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Never retry encryption with the same key/index after a failed write.
            if (_transfers.TryGetValue(received.Chunk.AttachmentId, out var state) &&
                state.OwnerDeviceId == received.DeviceId && state.SessionId == received.SessionId &&
                _transfers.TryRemove(received.Chunk.AttachmentId, out _))
                await state.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally { _operations.Release(); }
    }

    private async Task HandleChunkCoreAsync(
        ReceivedAttachmentChunk received, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(received);
        var payload = received.Chunk.Payload;
        try
        {
            if (!_transfers.TryGetValue(received.Chunk.AttachmentId, out var state) ||
                state.OwnerDeviceId != received.DeviceId ||
                state.SessionId != received.SessionId)
            {
                throw new InvalidDataException("The attachment transfer is unknown or belongs to another session.");
            }

            await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (received.Chunk.ChunkIndex != state.NextChunkIndex ||
                    state.BytesReceived + payload.Length > state.Offer.ByteCount ||
                    (payload.Length != state.Offer.ChunkSize &&
                     state.BytesReceived + payload.Length != state.Offer.ByteCount))
                {
                    throw new InvalidDataException("The attachment chunk is out of order or has an invalid size.");
                }

                await ChunkEncryptedAttachment.WriteChunkAsync(state.Stream, state.Header, state.Key,
                    state.ExpectedHash, state.NextChunkIndex, payload, cancellationToken).ConfigureAwait(false);
                state.Hash.AppendData(payload);
                state.BytesReceived += payload.Length;
                state.NextChunkIndex++;
            }
            finally
            {
                state.Gate.Release();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public async Task CopyDecryptedAsync(
        ImportedAttachment attachment,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite ||
            !string.Equals(
                Path.GetFileName(attachment.StorageName),
                attachment.StorageName,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The attachment export target or storage record is invalid.");
        }

        var encryptedPath = Path.GetFullPath(Path.Combine(_attachmentDirectory, attachment.StorageName));
        var requiredPrefix = _attachmentDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!encryptedPath.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The attachment storage path escaped its private directory.");
        }

        await using var source = new FileStream(encryptedPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var magic = new byte[4];
        await source.ReadExactlyAsync(magic, cancellationToken).ConfigureAwait(false);
        source.Position = 0;
        if (magic.AsSpan().SequenceEqual("PLA2"u8))
        {
            using var chunkKey = await _secretStore
                .RetrieveAsync(new SecretName(attachment.ProtectedKeyReference), cancellationToken).ConfigureAwait(false)
                ?? throw new CryptographicException("The protected attachment key is unavailable.");
            // Verify the entire file before exposing even a partial plaintext result.
            await ChunkEncryptedAttachment.CopyDecryptedAsync(source, Stream.Null, chunkKey.Bytes,
                attachment.EncryptionNonce, attachment.ContentSha256, attachment.ByteCount, cancellationToken).ConfigureAwait(false);
            source.Position = 0;
            await ChunkEncryptedAttachment.CopyDecryptedAsync(source, destination, chunkKey.Bytes,
                attachment.EncryptionNonce, attachment.ContentSha256, attachment.ByteCount, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        // Existing PLA1 imports remain readable, but never allocate from untrusted file length.
        if (attachment.ByteCount is <= 0 or > MaximumAttachmentBytes || source.Length != 32 + attachment.ByteCount)
            throw new CryptographicException("The legacy attachment size is invalid.");
        var encrypted = new byte[checked((int)source.Length)];
        await source.ReadExactlyAsync(encrypted, cancellationToken).ConfigureAwait(false);
        byte[]? plaintext = null;
        try
        {
            if (encrypted.Length != 32 + attachment.ByteCount ||
                !encrypted.AsSpan(0, 4).SequenceEqual("PLA1"u8) ||
                !encrypted.AsSpan(4, 12).SequenceEqual(attachment.EncryptionNonce))
            {
                throw new CryptographicException("The encrypted attachment header is invalid.");
            }

            using var key = await _secretStore
                .RetrieveAsync(new SecretName(attachment.ProtectedKeyReference), cancellationToken)
                .ConfigureAwait(false)
                ?? throw new CryptographicException("The protected attachment key is unavailable.");
            if (key.Bytes.Length != 32)
            {
                throw new CryptographicException("The protected attachment key has an invalid length.");
            }

            plaintext = new byte[checked((int)attachment.ByteCount)];
            using (var aes = new AesGcm(key.Bytes.Span, 16))
            {
                aes.Decrypt(
                    encrypted.AsSpan(4, 12),
                    encrypted.AsSpan(32),
                    encrypted.AsSpan(16, 16),
                    plaintext,
                    attachment.ContentSha256);
            }
            var actualHash = SHA256.HashData(plaintext);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, attachment.ContentSha256))
            {
                throw new CryptographicException("The decrypted attachment failed its final integrity check.");
            }

            await destination.WriteAsync(plaintext, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var transfer in _transfers.Values)
                await transfer.DisposeAsync().ConfigureAwait(false);
            _transfers.Clear();
        }
        finally { _operations.Release(); }
    }

    public async Task PruneSessionsAsync(Func<PhoneLink.Domain.Devices.DeviceId, Guid, bool> isActive)
    {
        ArgumentNullException.ThrowIfNull(isActive);
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            foreach (var pair in _transfers)
                if (!isActive(pair.Value.OwnerDeviceId, pair.Value.SessionId) &&
                    _transfers.TryRemove(pair.Key, out var state))
                    await state.DisposeAsync().ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    private async Task<AttachmentControlResponse> HandleOfferAsync(
        ReceivedDeviceEnvelope received,
        CancellationToken cancellationToken)
    {
        var offer = received.Envelope.Payload.Deserialize<AttachmentOfferPayload>(JsonOptions)
            ?? throw new InvalidDataException("An attachment offer cannot be null.");
        ValidateOffer(offer);

        if (_transfers.ContainsKey(offer.AttachmentId) ||
            File.Exists(Path.Combine(_attachmentDirectory, $"{offer.AttachmentId:N}.pla")))
        {
            return Rejected(offer.AttachmentId, "attachment.duplicate");
        }
        if (_transfers.Count >= 4 || _transfers.Values.Count(state => state.OwnerDeviceId == received.DeviceId) >= 2)
            return Rejected(offer.AttachmentId, "attachment.too-many-transfers");
        var root = Path.GetPathRoot(_attachmentDirectory)
            ?? throw new InvalidOperationException("The attachment directory has no storage root.");
        var freeSpace = new DriveInfo(root).AvailableFreeSpace;
        if (freeSpace < offer.ByteCount * 2 + 50L * 1024 * 1024)
        {
            return Rejected(offer.AttachmentId, "attachment.insufficient-space");
        }

        var expectedHash = Base64Url.Decode(offer.ContentSha256);
        if (expectedHash.Length != 32)
        {
            throw new InvalidDataException("The attachment content hash must be SHA-256.");
        }
        var temporaryPath = Path.Combine(_attachmentDirectory, $".{offer.AttachmentId:N}.incoming");
        var stream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            offer.ChunkSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        nonce.AsSpan(8, 4).Clear();
        var header = ChunkEncryptedAttachment.CreateHeader(offer.ChunkSize, offer.ByteCount, nonce);
        var state = new TransferState(
            received.DeviceId,
            received.Envelope.SessionId,
            offer,
            expectedHash,
            temporaryPath,
            stream, key, nonce, header, _secretStore);
        try
        {
            await _secretStore.StoreAsync(state.KeyName, key, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await state.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        if (!_transfers.TryAdd(offer.AttachmentId, state))
        {
            await state.DisposeAsync().ConfigureAwait(false);
            return Rejected(offer.AttachmentId, "attachment.duplicate");
        }

        return new AttachmentControlResponse(
            "attachment.accept",
            new AttachmentAcceptPayload
            {
                AttachmentId = offer.AttachmentId,
                Accepted = true,
                NextChunkIndex = 0,
            });
    }

    private async Task<AttachmentControlResponse> HandleCompleteAsync(
        ReceivedDeviceEnvelope received,
        CancellationToken cancellationToken)
    {
        var complete = received.Envelope.Payload.Deserialize<AttachmentCompletePayload>(JsonOptions)
            ?? throw new InvalidDataException("An attachment completion message cannot be null.");
        if (!_transfers.TryGetValue(complete.AttachmentId, out var state) ||
            state.OwnerDeviceId != received.DeviceId ||
            state.SessionId != received.Envelope.SessionId)
        {
            throw new InvalidDataException("The attachment transfer is unknown or belongs to another session.");
        }
        if (!_transfers.TryRemove(new KeyValuePair<Guid, TransferState>(complete.AttachmentId, state)))
            throw new InvalidDataException("The attachment transfer has already finished.");

        await state.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await state.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await state.Stream.DisposeAsync().ConfigureAwait(false);
            var actualHash = state.Hash.GetHashAndReset();
            if (state.BytesReceived != state.Offer.ByteCount ||
                !CryptographicOperations.FixedTimeEquals(actualHash, state.ExpectedHash))
            {
                return new AttachmentControlResponse(
                    "attachment.complete",
                    new AttachmentCompletePayload
                    {
                        AttachmentId = complete.AttachmentId,
                        Verified = false,
                        ErrorCode = "attachment.integrity-failed",
                    });
            }

            await CatalogEncryptedAsync(state, cancellationToken).ConfigureAwait(false);
            return new AttachmentControlResponse(
                "attachment.complete",
                new AttachmentCompletePayload
                {
                    AttachmentId = complete.AttachmentId,
                    Verified = true,
                });
        }
        finally
        {
            state.Gate.Release();
            await state.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task CatalogEncryptedAsync(
        TransferState state,
        CancellationToken cancellationToken)
    {
        var storageName = $"{state.Offer.AttachmentId:N}.pla";
        var destination = Path.Combine(_attachmentDirectory, storageName);
        File.Move(state.TemporaryPath, destination, overwrite: false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            await _attachmentStore.SaveVerifiedAsync(
                    new ImportedAttachment(
                        state.Offer.AttachmentId,
                        state.OwnerDeviceId,
                        storageName,
                        state.Offer.DisplayName,
                        state.Offer.MediaType,
                        state.Offer.ByteCount,
                        state.ExpectedHash.ToArray(),
                        state.KeyName.Value,
                        state.Nonce.ToArray(),
                        now,
                        now),
                cancellationToken).ConfigureAwait(false);
            state.Cataloged = true;
        }
        catch
        {
            File.Delete(destination);
            throw;
        }
    }

    private static void ValidateOffer(AttachmentOfferPayload offer)
    {
        if (offer.AttachmentId == Guid.Empty ||
            string.IsNullOrWhiteSpace(offer.DisplayName) ||
            offer.DisplayName.Length > 500 ||
            !AllowedMediaTypes.Contains(offer.MediaType) ||
            offer.ByteCount is <= 0 or > MaximumAttachmentBytes ||
            offer.ChunkSize is < 16_384 or > AttachmentBinaryFrameCodec.MaximumChunkBytes)
        {
            throw new InvalidDataException("The attachment offer violates the transfer policy.");
        }
    }

    private static AttachmentControlResponse Rejected(Guid attachmentId, string errorCode) =>
        new(
            "attachment.accept",
            new AttachmentAcceptPayload
            {
                AttachmentId = attachmentId,
                Accepted = false,
                NextChunkIndex = 0,
                ErrorCode = errorCode,
            });

    private sealed class TransferState : IAsyncDisposable
    {
        private bool _disposed;

        public TransferState(
            PhoneLink.Domain.Devices.DeviceId ownerDeviceId,
            Guid sessionId,
            AttachmentOfferPayload offer,
            byte[] expectedHash,
            string temporaryPath,
            FileStream stream, byte[] key, byte[] nonce, byte[] header, ISecretStore secretStore)
        {
            OwnerDeviceId = ownerDeviceId;
            SessionId = sessionId;
            Offer = offer;
            ExpectedHash = expectedHash;
            TemporaryPath = temporaryPath;
            Stream = stream;
            Key = key;
            Nonce = nonce;
            Header = header;
            SecretStore = secretStore;
        }

        public PhoneLink.Domain.Devices.DeviceId OwnerDeviceId { get; }
        public Guid SessionId { get; }
        public AttachmentOfferPayload Offer { get; }
        public byte[] ExpectedHash { get; }
        public string TemporaryPath { get; }
        public FileStream Stream { get; }
        public byte[] Key { get; }
        public byte[] Nonce { get; }
        public byte[] Header { get; }
        public ISecretStore SecretStore { get; }
        public bool Cataloged { get; set; }
        public SecretName KeyName { get; } = new($"attachment/file-key-v2/{Guid.NewGuid():D}");
        public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public long BytesReceived { get; set; }
        public uint NextChunkIndex { get; set; }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            await Stream.DisposeAsync().ConfigureAwait(false);
            Hash.Dispose();
            Gate.Dispose();
            CryptographicOperations.ZeroMemory(ExpectedHash);
            CryptographicOperations.ZeroMemory(Key);
            if (!Cataloged)
                await SecretStore.DeleteAsync(KeyName, CancellationToken.None).ConfigureAwait(false);
            if (File.Exists(TemporaryPath))
            {
                File.Delete(TemporaryPath);
            }
        }
    }
}
