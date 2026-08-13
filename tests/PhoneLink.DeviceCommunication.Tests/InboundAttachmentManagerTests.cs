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
        Assert.Equal("PLA1", Encoding.ASCII.GetString(encrypted, 0, 4));
        Assert.DoesNotContain("pppppppp", Encoding.UTF8.GetString(encrypted));
        using var key = await secretStore.RetrieveAsync(
            new SecretName(saved.ProtectedKeyReference),
            CancellationToken.None);
        Assert.NotNull(key);
        var decrypted = new byte[saved.ByteCount];
        using (var aes = new AesGcm(key.Bytes.Span, tagSizeInBytes: 16))
        {
            aes.Decrypt(
                encrypted.AsSpan(4, 12),
                encrypted.AsSpan(32),
                encrypted.AsSpan(16, 16),
                decrypted,
                contentHash);
        }
        Assert.Equal(photo, decrypted);

        await using var exported = new MemoryStream();
        await manager.CopyDecryptedAsync(saved, exported, CancellationToken.None);
        Assert.Equal(photo, exported.ToArray());
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
