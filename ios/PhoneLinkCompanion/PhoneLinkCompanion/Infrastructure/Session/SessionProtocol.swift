import Foundation

struct SessionClientHello: Codable {
    let kind: String
    let connectionId: UUID
    let clientDeviceId: UUID
    let clientNonce: String
    let protocolFloor: Int
    let protocolCeiling: Int
    let appVersion: String
}

struct SessionServerChallenge: Codable {
    let kind: String
    let connectionId: UUID
    let sessionId: UUID
    let serverDeviceId: UUID
    let serverNonce: String
    let selectedProtocolVersion: Int
    let serverTime: String
    let signature: String
}

struct SessionClientProof: Codable {
    let kind: String
    let connectionId: UUID
    let sessionId: UUID
    let signature: String
}

struct SessionReady: Codable {
    let kind: String
    let sessionId: UUID
    let serverDeviceId: UUID
    let protocolVersion: Int
    let capabilities: [String]
    let maximumBatchEvents: Int
    let maximumAttachmentBytes: Int64
}

struct ProtocolEnvelope<Payload: Codable>: Codable {
    let protocolVersion: Int
    let messageId: UUID
    let correlationId: UUID?
    let sessionId: UUID
    let originDeviceId: UUID
    let originSequence: Int64
    let sentAt: String
    let kind: String
    let requiresAck: Bool
    let payload: Payload
}

struct ProtocolEnvelopeHeader: Codable {
    let protocolVersion: Int
    let messageId: UUID
    let correlationId: UUID?
    let sessionId: UUID
    let originDeviceId: UUID
    let originSequence: Int64
    let sentAt: String
    let kind: String
    let requiresAck: Bool
}

struct EmptyPayload: Codable {}

struct SyncAckPayload: Codable, Sendable {
    let batchId: UUID
    let originDeviceId: UUID
    let highestContiguousSequence: Int64
}

struct SyncCheckpointPayload: Codable, Sendable {
    let originDeviceId: UUID
    let highestContiguousSequence: Int64
}

struct ContactReconciliationPayload: Codable, Sendable {
    let authorizedSourceContactIds: [String]
}

struct AttachmentOfferPayload: Codable, Sendable {
    let attachmentId: UUID
    let displayName: String
    let mediaType: String
    let byteCount: Int64
    let contentSha256: String
    let chunkSize: Int
}

struct AttachmentAcceptPayload: Codable, Sendable {
    let attachmentId: UUID
    let accepted: Bool
    let nextChunkIndex: UInt32
    let errorCode: String?
}

struct AttachmentCompletePayload: Codable, Sendable {
    let attachmentId: UUID
    let verified: Bool
    let errorCode: String?
}

struct CommandRequestPayload: Codable, Sendable {
    let commandId: UUID
    let name: String
    let arguments: [String: String]
    let expiresAt: String
    let requiresUserConfirmation: Bool
}

struct CommandResultPayload: Codable, Sendable {
    let commandId: UUID
    let status: String
    let errorCode: String?
    let finishedAt: String
}

enum PhoneLinkJSON {
    static let encoder: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        return encoder
    }()

    static let decoder = JSONDecoder()

    static func timestamp(_ date: Date = Date()) -> String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.string(from: date)
    }
}
