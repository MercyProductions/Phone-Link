using System.Collections.Concurrent;
using PhoneLink.Protocol.Pairing;

namespace PhoneLink.DeviceCommunication.Pairing;

public sealed class SingleUsePairingOfferRegistry : IDisposable
{
    private readonly ConcurrentDictionary<Guid, PairingOfferArtifact> _offers = new();
    private bool _disposed;

    public void Register(PairingOfferArtifact artifact)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(artifact);

        if (!_offers.TryAdd(artifact.Offer.SessionId, artifact))
        {
            throw new InvalidOperationException("A pairing offer with the same session ID already exists.");
        }
    }

    public PairingOfferArtifact? Consume(Guid sessionId, DateTimeOffset now)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_offers.TryRemove(sessionId, out var artifact))
        {
            return null;
        }

        if (artifact.Offer.ExpiresAt <= now)
        {
            artifact.Dispose();
            return null;
        }

        return artifact;
    }

    public void RemoveExpired(DateTimeOffset now)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (var entry in _offers)
        {
            if (entry.Value.Offer.ExpiresAt <= now &&
                _offers.TryRemove(entry.Key, out var expired))
            {
                expired.Dispose();
            }
        }
    }

    public bool Cancel(Guid sessionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_offers.TryRemove(sessionId, out var artifact))
        {
            return false;
        }

        artifact.Dispose();
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var entry in _offers)
        {
            if (_offers.TryRemove(entry.Key, out var artifact))
            {
                artifact.Dispose();
            }
        }

        _disposed = true;
    }
}
