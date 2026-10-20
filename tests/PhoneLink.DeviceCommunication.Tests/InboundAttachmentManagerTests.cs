using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhoneLink.Application.Features.Attachments;
using PhoneLink.Application.Ports;
using PhoneLink.DeviceCommunication.Attachments;
using PhoneLink.DeviceCommunication.Sessions;
using PhoneLink.Domain.Devices;
using PhoneLink.Protocol;
using PhoneLink.Protocol.Attachments;
using PhoneLink.Protocol.Messages;
using PhoneLink.Protocol.Pairing;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class InboundAttachmentManagerTests : IDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "PhoneLink.AttachmentTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task VerifiesEncryptsAndCatalogsOrderedPhotoChunks()
    {
        var secretStore = new InMemorySecretStore();
        var catalog = new InMemoryAttachmentStore();
        await using var manager = new InboundAttachmentManager(_directory, secretStore, catalog);
        var deviceId = DeviceId.New();
        var sessionId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var photo = Encoding.UTF8.GetBytes(new string('p', 70_000));
        var contentHash = SHA256.HashData(photo);
        var offer = new AttachmentOfferPayload
        {
            AttachmentId = attachmentId,
            DisplayName = "selected-photo.jpg",
            MediaType = "image/jpeg",
            ByteCount = photo.Length,
            ContentSha256 = Base64Url.Encode(contentHash),
            ChunkSize = 32_768,
        };

        var offerResponse = await manager.HandleEnvelopeAsync(
            Envelope(deviceId, sessionId, "attachment.offer", offer),
            CancellationToken.None);
        Assert.NotNull(offerResponse);
        var acceptance = Assert.IsType<AttachmentAcceptPayload>(offerResponse.Payload);
        Assert.True(acceptance.Accepted);

        // A second authenticated device cannot remove the owner's transfer by guessing its ID.
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.HandleEnvelopeAsync(
            Envelope(DeviceId.New(), Guid.NewGuid(), "attachment.complete",
                new AttachmentCompletePayload { AttachmentId = attachmentId, Verified = false }),
            CancellationToken.None));
        var foreignChunk = AttachmentBinaryFrameCodec.Parse(AttachmentBinaryFrameCodec.Encode(
            attachmentId, 0, photo.AsSpan(0, offer.ChunkSize)));
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.HandleChunkAsync(
            new ReceivedAttachmentChunk(DeviceId.New(), Guid.NewGuid(), foreignChunk), CancellationToken.None));
        Assert.All(foreignChunk.Payload, value => Assert.Equal(0, value));

        uint index = 0;
        for (var offset = 0; offset < photo.Length; offset += offer.ChunkSize)
        {
            var length = Math.Min(offer.ChunkSize, photo.Length - offset);
            var encoded = AttachmentBinaryFrameCodec.Encode(
                attachmentId,
                index,
                photo.AsSpan(offset, length));
            await manager.HandleChunkAsync(
                new ReceivedAttachmentChunk(
                    deviceId,
                    sessionId,
                    AttachmentBinaryFrameCodec.Parse(encoded)),
                CancellationToken.None);
            index++;
        }

        var completeResponse = await manager.HandleEnvelopeAsync(
            Envelope(
                deviceId,
                sessionId,
                "attachment.complete",
                new AttachmentCompletePayload { AttachmentId = attachmentId, Verified = false }),
            CancellationToken.None);
        var completion = Assert.IsType<AttachmentCompletePayload>(completeResponse!.Payload);
        Assert.True(completion.Verified);

        var saved = Assert.Single(catalog.Attachments);
        var encrypted = await File.ReadAllBytesAsync(Path.Combine(_directory, saved.StorageName));
        Assert.Equal("PLA2", Encoding.ASCII.GetString(encrypted, 0, 4));
        Assert.DoesNotContain("pppppppp", Encoding.UTF8.GetString(encrypted));
        using var key = await secretStore.RetrieveAsync(
            new SecretName(saved.ProtectedKeyReference),
            CancellationToken.None);
        Assert.NotNull(key);
        await using var exported = new BoundedWriteStream();
        await manager.CopyDecryptedAsync(saved, exported, CancellationToken.None);
        Assert.Equal(photo, exported.ToArray());
        Assert.InRange(exported.MaximumWriteBytes, 1, offer.ChunkSize);

        // Header, ciphertext, truncation, extra data, and chunk reordering are all authenticated.
        foreach (var mutation in new[] { "header", "first", "last", "truncate", "append", "reorder" })
        {
            var corrupt = encrypted.ToArray();
            switch (mutation)
            {
                case "header": corrupt[16] ^= 1; break;
                case "first": corrupt[28] ^= 1; break;
                case "last": corrupt[^1] ^= 1; break;
                case "truncate": corrupt = corrupt[..^1]; break;
                case "append": corrupt = [.. corrupt, 0]; break;
                case "reorder":
                    var recordBytes = offer.ChunkSize + 16;
                    var first = corrupt.AsSpan(28, recordBytes).ToArray();
                    corrupt.AsSpan(28 + recordBytes, recordBytes).CopyTo(corrupt.AsSpan(28));
                    first.CopyTo(corrupt, 28 + recordBytes);
                    break;
            }
            await File.WriteAllBytesAsync(Path.Combine(_directory, saved.StorageName), corrupt);
            await using var rejectedExport = new MemoryStream();
            var failure = await Record.ExceptionAsync(() =>
                manager.CopyDecryptedAsync(saved, rejectedExport, CancellationToken.None));
            Assert.True(failure is CryptographicException or EndOfStreamException, mutation);
            Assert.Equal(0, rejectedExport.Length);
        }

        // Re-offering a completed ID cannot overwrite its protected encryption key.
        var duplicate = await manager.HandleEnvelopeAsync(
            Envelope(deviceId, sessionId, "attachment.offer", offer), CancellationToken.None);
        Assert.False(Assert.IsType<AttachmentAcceptPayload>(duplicate!.Payload).Accepted);
        await File.WriteAllBytesAsync(Path.Combine(_directory, saved.StorageName), encrypted);
        await using var stillReadable = new MemoryStream();
        await manager.CopyDecryptedAsync(saved, stillReadable, CancellationToken.None);
        Assert.Equal(photo, stillReadable.ToArray());
    }

    [Fact]
    public async Task DisconnectDiscardsEncryptedStagingAndProtectedKey()
    {
        var secrets = new InMemorySecretStore();
        await using var manager = new InboundAttachmentManager(_directory, secrets, new InMemoryAttachmentStore());
        var owner = DeviceId.New();
        var session = Guid.NewGuid();
        var offer = CreateOffer();
        await manager.HandleEnvelopeAsync(Envelope(owner, session, "attachment.offer", offer), CancellationToken.None);
        Assert.Single(Directory.GetFiles(_directory));
        Assert.Equal(1, secrets.Count);
        await manager.PruneSessionsAsync((device, id) => device == owner && id == session);
        Assert.Single(Directory.GetFiles(_directory));
        await manager.PruneSessionsAsync((_, _) => false);
        Assert.Empty(Directory.GetFiles(_directory));
        Assert.Equal(0, secrets.Count);
    }

    [Fact]
    public async Task InvalidChunkAbortsRatherThanReusingEncryptionNonce()
    {
        var secrets = new InMemorySecretStore();
        await using var manager = new InboundAttachmentManager(_directory, secrets, new InMemoryAttachmentStore());
        var owner = DeviceId.New();
        var session = Guid.NewGuid();
        var offer = CreateOffer();
        await manager.HandleEnvelopeAsync(Envelope(owner, session, "attachment.offer", offer), CancellationToken.None);
        var frame = AttachmentBinaryFrameCodec.Parse(
            AttachmentBinaryFrameCodec.Encode(offer.AttachmentId, 1, new byte[offer.ChunkSize]));
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.HandleChunkAsync(
            new ReceivedAttachmentChunk(owner, session, frame), CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_directory));
        Assert.Equal(0, secrets.Count);
        Assert.All(frame.Payload, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task LimitsConcurrentOffersPerDevice()
    {
        await using var manager = new InboundAttachmentManager(_directory, new InMemorySecretStore(), new InMemoryAttachmentStore());
        var owner = DeviceId.New();
        var session = Guid.NewGuid();
        for (var index = 0; index < 3; index++)
        {
            var response = await manager.HandleEnvelopeAsync(
                Envelope(owner, session, "attachment.offer", CreateOffer()), CancellationToken.None);
            Assert.Equal(index < 2, Assert.IsType<AttachmentAcceptPayload>(response!.Payload).Accepted);
        }
    }

    [Fact]
    public async Task LegacyImportsRemainReadable()
    {
        var secrets = new InMemorySecretStore();
        await using var manager = new InboundAttachmentManager(_directory, secrets, new InMemoryAttachmentStore());
        var plain = Encoding.UTF8.GetBytes("Previously imported photo");
        var hash = SHA256.HashData(plain);
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var encrypted = new byte[32 + plain.Length];
        "PLA1"u8.CopyTo(encrypted);
        nonce.CopyTo(encrypted, 4);
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, plain, encrypted.AsSpan(32), encrypted.AsSpan(16, 16), hash);
        var keyName = new SecretName("attachment/legacy-key");
        await secrets.StoreAsync(keyName, key, CancellationToken.None);
        await File.WriteAllBytesAsync(Path.Combine(_directory, "legacy.pla"), encrypted);
        var attachment = new ImportedAttachment(Guid.NewGuid(), DeviceId.New(), "legacy.pla", "Old.jpg", "image/jpeg",
            plain.Length, hash, keyName.Value, nonce, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await using var result = new MemoryStream();
        await manager.CopyDecryptedAsync(attachment, result, CancellationToken.None);
        Assert.Equal(plain, result.ToArray());
    }

    private static AttachmentOfferPayload CreateOffer() => new()
    {
        AttachmentId = Guid.NewGuid(), DisplayName = "Photo.jpg", MediaType = "image/jpeg",
        ByteCount = 32_768, ChunkSize = 32_768, ContentSha256 = Base64Url.Encode(SHA256.HashData(new byte[32_768])),
    };

    private sealed class BoundedWriteStream : MemoryStream
    {
        public int MaximumWriteBytes { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaximumWriteBytes = Math.Max(MaximumWriteBytes, buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static ReceivedDeviceEnvelope Envelope<T>(
        DeviceId deviceId,
        Guid sessionId,
        string kind,
        T payload) =>
        new(
            deviceId,
            new ProtocolEnvelope
            {
                ProtocolVersion = ProtocolConstants.CurrentVersion,
                MessageId = Guid.NewGuid(),
                SessionId = sessionId,
                OriginDeviceId = deviceId.Value,
                OriginSequence = 0,
                SentAt = DateTimeOffset.UtcNow,
                Kind = kind,
                RequiresAck = true,
                Payload = JsonSerializer.SerializeToElement(payload, WebJson),
            });

    private sealed class InMemoryAttachmentStore : IAttachmentStore
    {
        public List<ImportedAttachment> Attachments { get; } = [];

        public Task SaveVerifiedAsync(ImportedAttachment attachment, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attachments.Add(attachment);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ImportedAttachment>> ListAsync(
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ImportedAttachment>>(Attachments);
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly ConcurrentDictionary<SecretName, byte[]> _values = new();
        public int Count => _values.Count;

        public Task StoreAsync(SecretName name, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values[name] = value.ToArray();
            return Task.CompletedTask;
        }

        public Task<SecretValue?> RetrieveAsync(SecretName name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                _values.TryGetValue(name, out var value) ? new SecretValue(value) : null);
        }

        public Task DeleteAsync(SecretName name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.TryRemove(name, out _);
            return Task.CompletedTask;
        }
    }
}
