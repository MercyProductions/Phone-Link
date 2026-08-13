import CryptoKit
import Foundation

enum PairingTranscript {
    private static let clientDomain = Data("PhoneLink-Pairing-Client-v1".utf8)
    private static let serverDomain = Data("PhoneLink-Pairing-Server-v1".utf8)
    private static let compareDomain = Data("PhoneLink-Pairing-Compare-v1".utf8)

    static func clientDigest(
        rawOfferJSON: Data,
        clientDeviceID: String,
        clientIdentityPublicKey: Data,
        clientNonce: Data
    ) -> Data {
        digestLengthPrefixed([
            clientDomain,
            rawOfferJSON,
            Data(clientDeviceID.utf8),
            clientIdentityPublicKey,
            clientNonce,
        ])
    }

    static func clientProof(pairingSecret: Data, clientDigest: Data) -> Data {
        let authenticationCode = HMAC<SHA256>.authenticationCode(
            for: clientDigest,
            using: SymmetricKey(data: pairingSecret)
        )
        return Data(authenticationCode)
    }

    static func serverSignatureInput(
        rawOfferJSON: Data,
        clientDeviceID: String,
        clientIdentityPublicKey: Data,
        clientNonce: Data,
        serverNonce: Data
    ) -> Data {
        digestLengthPrefixed([
            serverDomain,
            rawOfferJSON,
            Data(clientDeviceID.utf8),
            clientIdentityPublicKey,
            clientNonce,
            serverNonce,
        ])
    }

    static func comparisonDigest(serverSignatureInput: Data, serverSignature: Data) -> Data {
        var hasher = SHA256()
        hasher.update(data: compareDomain)
        hasher.update(data: serverSignatureInput)
        hasher.update(data: serverSignature)
        return Data(hasher.finalize())
    }

    private static func digestLengthPrefixed(_ values: [Data]) -> Data {
        var hasher = SHA256()
        for value in values {
            var length = UInt32(value.count).bigEndian
            withUnsafeBytes(of: &length) { hasher.update(bufferPointer: $0) }
            hasher.update(data: value)
        }
        return Data(hasher.finalize())
    }
}
