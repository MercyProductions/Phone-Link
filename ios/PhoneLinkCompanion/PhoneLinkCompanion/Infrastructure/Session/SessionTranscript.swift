import CryptoKit
import Foundation

enum SessionTranscript {
    private static let serverContext = Data("phonelink/session/server-proof/v1".utf8)
    private static let clientContext = Data("phonelink/session/client-proof/v1".utf8)

    static func serverProofInput(
        connectionID: UUID,
        sessionID: UUID,
        serverDeviceID: UUID,
        clientDeviceID: UUID,
        clientNonce: Data,
        serverNonce: Data,
        selectedProtocolVersion: Int,
        clientAppVersion: String
    ) -> Data {
        var version = Int32(selectedProtocolVersion).bigEndian
        let versionData = withUnsafeBytes(of: &version) { Data($0) }
        return digestLengthPrefixed([
            serverContext,
            Data(connectionID.uuidString.lowercased().utf8),
            Data(sessionID.uuidString.lowercased().utf8),
            Data(serverDeviceID.uuidString.lowercased().utf8),
            Data(clientDeviceID.uuidString.lowercased().utf8),
            clientNonce,
            serverNonce,
            versionData,
            Data(clientAppVersion.utf8),
        ])
    }

    static func clientProofInput(serverProofInput: Data, serverSignature: Data) -> Data {
        digestLengthPrefixed([clientContext, serverProofInput, serverSignature])
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
