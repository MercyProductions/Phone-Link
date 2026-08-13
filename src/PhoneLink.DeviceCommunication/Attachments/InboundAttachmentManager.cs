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

                await state.Stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
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

        var encrypted = await File.ReadAllBytesAsync(encryptedPath, cancellationToken).ConfigureAwait(false);
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
        foreach (var transfer in _transfers.Values)
        {
            await transfer.DisposeAsync().ConfigureAwait(false);
        }
        _transfers.Clear();
    }

    private async Task<AttachmentControlResponse> HandleOfferAsync(
        ReceivedDeviceEnvelope received,
        CancellationToken cancellationToken)
    {
        var offer = received.Envelope.Payload.Deserialize<AttachmentOfferPayload>(JsonOptions)
            ?? throw new InvalidDataException("An attachment offer cannot be null.");
        ValidateOffer(offer);

        if (_transfers.ContainsKey(offer.AttachmentId))
        {
            return Rejected(offer.AttachmentId, "attachment.duplicate");
        }
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
        var state = new TransferState(
            received.DeviceId,
            received.Envelope.SessionId,
            offer,
            expectedHash,
            temporaryPath,
            stream);
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
        if (!_transfers.TryRemove(complete.AttachmentId, out var state) ||
            state.OwnerDeviceId != received.DeviceId ||
            state.SessionId != received.Envelope.SessionId)
        {
            throw new InvalidDataException("The attachment transfer is unknown or belongs to another session.");
        }

        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
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

            await EncryptAndCatalogAsync(state, cancellationToken).ConfigureAwait(false);
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

    private async Task EncryptAndCatalogAsync(
        TransferState state,
        CancellationToken cancellationToken)
    {
        var plaintext = await File.ReadAllBytesAsync(state.TemporaryPath, cancellationToken).ConfigureAwait(false);
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        var storageName = $"{state.Offer.AttachmentId:N}.pla";
        var destination = Path.Combine(_attachmentDirectory, storageName);
        var temporaryEncrypted = $"{destination}.tmp";
        var keyName = new SecretName($"attachment/{state.Offer.AttachmentId:D}/file-key-v1");
        try
        {
            using (var aes = new AesGcm(key, tag.Length))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag, state.ExpectedHash);
            }

            await using (var stream = new FileStream(
                temporaryEncrypted,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync("PLA1"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(nonce, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(ciphertext, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await _secretStore.StoreAsync(keyName, key, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryEncrypted, destination, overwrite: false);
            var now = DateTimeOffset.UtcNow;
            try
            {
                await _attachmentStore.SaveVerifiedAsync(
                    new ImportedAttachment(
                        state.Offer.AttachmentId,
                        state.OwnerDeviceId,
                        storageName,
                        state.Offer.DisplayName,
                        state.Offer.MediaType,
                        state.Offer.ByteCount,
                        state.ExpectedHash.ToArray(),
                        keyName.Value,
                        nonce.ToArray(),
                        now,
                        now),
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                File.Delete(destination);
                await _secretStore.DeleteAsync(keyName, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(ciphertext);
            if (File.Exists(temporaryEncrypted))
            {
                File.Delete(temporaryEncrypted);
            }
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
            FileStream stream)
        {
            OwnerDeviceId = ownerDeviceId;
            SessionId = sessionId;
            Offer = offer;
            ExpectedHash = expectedHash;
            TemporaryPath = temporaryPath;
            Stream = stream;
        }

        public PhoneLink.Domain.Devices.DeviceId OwnerDeviceId { get; }
        public Guid SessionId { get; }
        public AttachmentOfferPayload Offer { get; }
        public byte[] ExpectedHash { get; }
        public string TemporaryPath { get; }
        public FileStream Stream { get; }
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
            if (File.Exists(TemporaryPath))
            {
                File.Delete(TemporaryPath);
            }
        }
    }
}
