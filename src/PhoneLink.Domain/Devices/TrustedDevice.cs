namespace PhoneLink.Domain.Devices;

public sealed class TrustedDevice
{
    public const int IdentityPublicKeyLength = 32;
    public const int MaximumDisplayNameLength = 200;

    private readonly byte[] _identityPublicKey;

    private TrustedDevice(
        DeviceId id,
        string displayName,
        DevicePlatform platform,
        byte[] identityPublicKey,
        DateTimeOffset pairedAt,
        DeviceTrustState trustState,
        DateTimeOffset? revokedAt)
    {
        Id = id;
        DisplayName = displayName;
        Platform = platform;
        _identityPublicKey = identityPublicKey;
        PairedAt = pairedAt;
        TrustState = trustState;
        RevokedAt = revokedAt;
    }

    public DeviceId Id { get; }

    public string DisplayName { get; }

    public DevicePlatform Platform { get; }

    public ReadOnlyMemory<byte> IdentityPublicKey => _identityPublicKey;

    public DateTimeOffset PairedAt { get; }

    public DeviceTrustState TrustState { get; }

    public DateTimeOffset? RevokedAt { get; }

    public static TrustedDevice Create(
        DeviceId id,
        string displayName,
        DevicePlatform platform,
        ReadOnlySpan<byte> identityPublicKey,
        DateTimeOffset pairedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        var normalizedName = displayName.Trim();
        if (normalizedName.Length > MaximumDisplayNameLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(displayName),
                $"A device name cannot exceed {MaximumDisplayNameLength} characters.");
        }

        if (identityPublicKey.Length != IdentityPublicKeyLength)
        {
            throw new ArgumentException(
                $"An Ed25519 public key must be {IdentityPublicKeyLength} bytes.",
                nameof(identityPublicKey));
        }

        return new TrustedDevice(
            id,
            normalizedName,
            platform,
            identityPublicKey.ToArray(),
            pairedAt,
            DeviceTrustState.Trusted,
            revokedAt: null);
    }

    public TrustedDevice Revoke(DateTimeOffset revokedAt)
    {
        if (TrustState == DeviceTrustState.Revoked)
        {
            return this;
        }

        if (revokedAt < PairedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revokedAt),
                "A device cannot be revoked before it was paired.");
        }

        return new TrustedDevice(
            Id,
            DisplayName,
            Platform,
            _identityPublicKey.ToArray(),
            PairedAt,
            DeviceTrustState.Revoked,
            revokedAt);
    }
}

