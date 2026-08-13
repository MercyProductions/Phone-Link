import CryptoKit
import Foundation
import Network

struct PairingEndpoint: Codable, Equatable {
    let host: String
    let port: UInt16
}

struct PairingOffer: Equatable {
    let version: Int
    let sessionID: UUID
    let expiresAt: Date
    let endpoints: [PairingEndpoint]
    let tlsSPKISHA256: Data
    let serverIdentityKey: Data
    let offerNonce: Data
    let pairingSecret: Data
}

struct PairingOfferArtifact: Equatable {
    let offer: PairingOffer
    let rawJSON: Data
    let qrPayload: String

    var serverFingerprint: String {
        IdentityFingerprint.display(offer.serverIdentityKey)
    }
}

enum PairingOfferCodec {
    private static let prefix = "phonelink://pair?data="
    private static let expectedRootKeys: Set<String> = [
        "version", "sessionId", "expiresAt", "endpoints", "tlsSpkiSha256",
        "serverIdentityKey", "offerNonce", "pairingSecret",
    ]
    private static let expectedEndpointKeys: Set<String> = ["host", "port"]

    static func decode(_ qrPayload: String, now: Date = Date()) throws -> PairingOfferArtifact {
        guard qrPayload.hasPrefix(prefix) else { throw PairingOfferError.invalidURI }
        let encoded = String(qrPayload.dropFirst(prefix.count))
        guard !encoded.contains(where: { "?#&=".contains($0) }) else {
            throw PairingOfferError.invalidURI
        }

        let rawJSON = try Base64URL.decode(encoded)
        guard rawJSON.count <= 16_384 else { throw PairingOfferError.offerTooLarge }
        try StrictJSON.validateNoDuplicateObjectKeys(rawJSON)
        try validateProperties(rawJSON)

        let dto: PairingOfferDTO
        do {
            dto = try JSONDecoder().decode(PairingOfferDTO.self, from: rawJSON)
        } catch {
            throw PairingOfferError.invalidJSON
        }

        guard dto.version == 1 else { throw PairingOfferError.unsupportedVersion }
        guard let sessionID = UUID(uuidString: dto.sessionId),
              dto.sessionId == sessionID.uuidString.lowercased() else {
            throw PairingOfferError.invalidSessionID
        }

        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        guard let expiresAt = formatter.date(from: dto.expiresAt) else {
            throw PairingOfferError.invalidExpiry
        }
        guard expiresAt > now, expiresAt.timeIntervalSince(now) <= 180 else {
            throw PairingOfferError.expired
        }

        guard (1...8).contains(dto.endpoints.count),
              dto.endpoints.allSatisfy({ isLocalEndpoint($0.host) }) else {
            throw PairingOfferError.nonLocalEndpoint
        }

        let tlsPin = try Base64URL.decode(dto.tlsSpkiSha256)
        let identityKey = try Base64URL.decode(dto.serverIdentityKey)
        let offerNonce = try Base64URL.decode(dto.offerNonce)
        let secret = try Base64URL.decode(dto.pairingSecret)
        guard tlsPin.count == 32,
              identityKey.count == 32,
              offerNonce.count == 16,
              secret.count == 32 else {
            throw PairingOfferError.invalidFieldLength
        }

        return PairingOfferArtifact(
            offer: PairingOffer(
                version: dto.version,
                sessionID: sessionID,
                expiresAt: expiresAt,
                endpoints: dto.endpoints,
                tlsSPKISHA256: tlsPin,
                serverIdentityKey: identityKey,
                offerNonce: offerNonce,
                pairingSecret: secret
            ),
            rawJSON: rawJSON,
            qrPayload: qrPayload
        )
    }

    private static func validateProperties(_ data: Data) throws {
        guard let root = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              Set(root.keys) == expectedRootKeys,
              let endpoints = root["endpoints"] as? [[String: Any]],
              endpoints.allSatisfy({ Set($0.keys) == expectedEndpointKeys }) else {
            throw PairingOfferError.unexpectedProperties
        }
    }

    private static func isLocalEndpoint(_ host: String) -> Bool {
        if host.lowercased().hasSuffix(".local") { return true }

        if let address = IPv4Address(host) {
            let bytes = [UInt8](address.rawValue)
            guard bytes.count == 4, bytes[0] != 127 else { return false }
            return bytes[0] == 10 ||
                (bytes[0] == 172 && (16...31).contains(bytes[1])) ||
                (bytes[0] == 192 && bytes[1] == 168) ||
                (bytes[0] == 169 && bytes[1] == 254)
        }

        if let address = IPv6Address(host) {
            let bytes = [UInt8](address.rawValue)
            guard bytes.count == 16 else { return false }
            return (bytes[0] & 0xFE) == 0xFC ||
                (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
        }

        return false
    }
}

private struct PairingOfferDTO: Decodable {
    let version: Int
    let sessionId: String
    let expiresAt: String
    let endpoints: [PairingEndpoint]
    let tlsSpkiSha256: String
    let serverIdentityKey: String
    let offerNonce: String
    let pairingSecret: String
}

enum PairingOfferError: LocalizedError, Equatable {
    case invalidURI
    case invalidBase64URL
    case offerTooLarge
    case invalidJSON
    case duplicateJSONProperty(String)
    case unexpectedProperties
    case unsupportedVersion
    case invalidSessionID
    case invalidExpiry
    case expired
    case nonLocalEndpoint
    case invalidFieldLength

    var errorDescription: String? {
        switch self {
        case .invalidURI: "This is not a Phone Link pairing QR code."
        case .invalidBase64URL: "The QR code contains invalid encoded data."
        case .offerTooLarge: "The pairing offer is larger than allowed."
        case .invalidJSON: "The pairing offer is not valid JSON."
        case .duplicateJSONProperty(let name): "The pairing offer repeats the field ‘\(name)’."
        case .unexpectedProperties: "The pairing offer has missing or unsupported fields."
        case .unsupportedVersion: "This Phone Link protocol version is not supported."
        case .invalidSessionID: "The pairing session identifier is invalid."
        case .invalidExpiry: "The pairing offer expiry is invalid."
        case .expired: "The pairing QR code has expired. Ask Windows to show a new one."
        case .nonLocalEndpoint: "The pairing offer does not point to a local network address."
        case .invalidFieldLength: "A cryptographic field in the pairing offer has the wrong length."
        }
    }
}
