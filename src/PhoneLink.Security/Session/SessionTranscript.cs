using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PhoneLink.Security.Session;

public static class SessionTranscript
{
    private static readonly byte[] ServerContext =
        "phonelink/session/server-proof/v1"u8.ToArray();
    private static readonly byte[] ClientContext =
        "phonelink/session/client-proof/v1"u8.ToArray();

    public static byte[] CreateServerProofInput(
        Guid connectionId,
        Guid sessionId,
        Guid serverDeviceId,
        Guid clientDeviceId,
        ReadOnlySpan<byte> clientNonce,
        ReadOnlySpan<byte> serverNonce,
        int selectedProtocolVersion,
        string clientAppVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientAppVersion);
        RequireNonce(clientNonce, nameof(clientNonce));
        RequireNonce(serverNonce, nameof(serverNonce));

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, ServerContext);
        Append(hash, Encoding.UTF8.GetBytes(connectionId.ToString("D")));
        Append(hash, Encoding.UTF8.GetBytes(sessionId.ToString("D")));
        Append(hash, Encoding.UTF8.GetBytes(serverDeviceId.ToString("D")));
        Append(hash, Encoding.UTF8.GetBytes(clientDeviceId.ToString("D")));
        Append(hash, clientNonce);
        Append(hash, serverNonce);

        Span<byte> version = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(version, selectedProtocolVersion);
        Append(hash, version);
        Append(hash, Encoding.UTF8.GetBytes(clientAppVersion));
        return hash.GetHashAndReset();
    }

    public static byte[] CreateClientProofInput(
        ReadOnlySpan<byte> serverProofInput,
        ReadOnlySpan<byte> serverSignature)
    {
        if (serverProofInput.Length != 32)
        {
            throw new ArgumentException("The server proof input must be a SHA-256 digest.", nameof(serverProofInput));
        }

        if (serverSignature.Length != 64)
        {
            throw new ArgumentException("An Ed25519 signature must be 64 bytes.", nameof(serverSignature));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, ClientContext);
        Append(hash, serverProofInput);
        Append(hash, serverSignature);
        return hash.GetHashAndReset();
    }

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }

    private static void RequireNonce(ReadOnlySpan<byte> value, string parameterName)
    {
        if (value.Length != 32)
        {
            throw new ArgumentException("Session nonces must be 32 bytes.", parameterName);
        }
    }
}
