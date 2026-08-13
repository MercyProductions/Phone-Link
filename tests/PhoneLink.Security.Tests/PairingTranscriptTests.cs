using System.Security.Cryptography;
using PhoneLink.Security.Pairing;

namespace PhoneLink.Security.Tests;

public sealed class PairingTranscriptTests
{
    private const string RawOfferBase64 = "eyJ2ZXJzaW9uIjoxLCJzZXNzaW9uSWQiOiI0YWJiZTMxMi1lODFlLTQ1ZWMtOGM4ZS02YTQ3NDE2MWFkN2YiLCJleHBpcmVzQXQiOiIyMDI2LTA4LTEyVDE5OjA2OjMxLjQyN1oiLCJlbmRwb2ludHMiOlt7Imhvc3QiOiIxOTIuMTY4LjEuMjQiLCJwb3J0Ijo0NTgzMX1dLCJ0bHNTcGtpU2hhMjU2IjoiRVJFUkVSRVJFUkVSRVJFUkVSRVJFUkVSRVJFUkVSRVJFUkVSRVJFUkVSRSIsInNlcnZlcklkZW50aXR5S2V5IjoiSWlJaUlpSWlJaUlpSWlJaUlpSWlJaUlpSWlJaUlpSWlJaUlpSWlJaUlpSSIsIm9mZmVyTm9uY2UiOiJNek16TXpNek16TXpNek16TXpNek13IiwicGFpcmluZ1NlY3JldCI6IlJFUkVSRVJFUkVSRVJFUkVSRVJFUkVSRVJFUkVSRVJFUkVSRVJFUkVSRVEifQ==";

    [Fact]
    public void TranscriptMatchesIosVector()
    {
        var rawOffer = Convert.FromBase64String(RawOfferBase64);
        var clientPublicKey = Enumerable.Repeat((byte)0x55, 32).ToArray();
        var clientNonce = Enumerable.Repeat((byte)0x66, 32).ToArray();
        var serverNonce = Enumerable.Repeat((byte)0x77, 32).ToArray();
        var signature = Enumerable.Repeat((byte)0x88, 64).ToArray();
        var secret = Enumerable.Repeat((byte)0x44, 32).ToArray();
        const string clientDeviceId = "ca8cf3a6-e6ab-4ac7-be7f-6b5736a14ec3";

        var clientDigest = PairingTranscript.CreateClientDigest(
            rawOffer,
            clientDeviceId,
            clientPublicKey,
            clientNonce);
        Assert.Equal(
            "796cae15a04eb711c6437fcc6fd2eb0fa21cbf8af08d59717815b8e2ec880a84",
            Convert.ToHexStringLower(clientDigest));

        var proof = PairingTranscript.CreateClientProof(secret, clientDigest);
        Assert.Equal(
            "18bbde968f778ee8c353b9ae5fd20d4da3942781fee21407967d9f4369a192b9",
            Convert.ToHexStringLower(proof));

        var serverInput = PairingTranscript.CreateServerSignatureInput(
            rawOffer,
            clientDeviceId,
            clientPublicKey,
            clientNonce,
            serverNonce);
        Assert.Equal(
            "83c205aa19830177b1d069c221648b4dc61ffe4d9d7aefafa478fc91b607a4ba",
            Convert.ToHexStringLower(serverInput));

        var comparisonDigest = PairingTranscript.CreateComparisonDigest(serverInput, signature);
        Assert.Equal(
            "harbor-moon dawn-elm bold-meadow misty-field harbor-falcon aqua-elm",
            ComparisonPhrase.Create(comparisonDigest));
    }

    [Fact]
    public void ClientProofRejectsAnyTranscriptChange()
    {
        var offer = "offer"u8;
        var publicKey = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var nonce = Enumerable.Repeat((byte)0xA5, 32).ToArray();
        var secret = Enumerable.Repeat((byte)0x4C, 32).ToArray();
        var digest = PairingTranscript.CreateClientDigest(offer, "phone-1", publicKey, nonce);
        var proof = PairingTranscript.CreateClientProof(secret, digest);

        Assert.True(PairingTranscript.VerifyClientProof(secret, digest, proof));

        digest[0] ^= 0x01;
        Assert.False(PairingTranscript.VerifyClientProof(secret, digest, proof));
    }

    [Fact]
    public void ComparisonPhraseContainsSixStableCodeWords()
    {
        var digest = SHA256.HashData("comparison input"u8);

        var first = ComparisonPhrase.Create(digest);
        var second = ComparisonPhrase.Create(digest);

        Assert.Equal(first, second);
        Assert.Equal(6, first.Split(' ').Length);
        Assert.All(first.Split(' '), word => Assert.Contains('-', word));
    }

    [Fact]
    public void Ed25519IdentityRoundTripsAndVerifies()
    {
        using var original = Ed25519Identity.Create();
        var privateKey = original.ExportPrivateKey();
        var publicKey = original.ExportPublicKey();
        var data = "signed transcript"u8;
        var signature = original.Sign(data);

        try
        {
            Assert.True(Ed25519Identity.Verify(publicKey, data, signature));

            using var imported = Ed25519Identity.ImportPrivateKey(privateKey);
            Assert.Equal(publicKey, imported.ExportPublicKey());
            Assert.True(Ed25519Identity.Verify(publicKey, data, imported.Sign(data)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }
}
