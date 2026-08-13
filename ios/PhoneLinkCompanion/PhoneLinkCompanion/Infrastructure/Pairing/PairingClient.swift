import CryptoKit
import Foundation
import Security
import UIKit

@MainActor
final class PairingClient {
    private let identityStore: DeviceIdentityStore
    private let trustedPeerStore: TrustedWindowsPeerStore

    init(
        identityStore: DeviceIdentityStore = DeviceIdentityStore(),
        trustedPeerStore: TrustedWindowsPeerStore = TrustedWindowsPeerStore()
    ) {
        self.identityStore = identityStore
        self.trustedPeerStore = trustedPeerStore
    }

    func connect(to artifact: PairingOfferArtifact) async throws -> PendingPairingConnection {
        let identity = try identityStore.loadOrCreate()
        let deviceID = try identityStore.loadOrCreateDeviceID()
        let clientNonce = try randomData(count: 32)
        let clientDigest = PairingTranscript.clientDigest(
            rawOfferJSON: artifact.rawJSON,
            clientDeviceID: deviceID.uuidString.lowercased(),
            clientIdentityPublicKey: identity.publicKey,
            clientNonce: clientNonce
        )
        let proof = PairingTranscript.clientProof(
            pairingSecret: artifact.offer.pairingSecret,
            clientDigest: clientDigest
        )

        let hello = PairingClientHello(
            kind: "pairing.clientHello",
            sessionId: artifact.offer.sessionID,
            clientDeviceId: deviceID,
            clientDisplayName: String(UIDevice.current.name.prefix(200)),
            clientIdentityPublicKey: Base64URL.encode(identity.publicKey),
            clientNonce: Base64URL.encode(clientNonce),
            proof: Base64URL.encode(proof),
            appVersion: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
        )

        var lastError: Error = PairingClientError.noReachableEndpoint
        for endpoint in artifact.offer.endpoints {
            do {
                let delegate = PinnedTLSDelegate(expectedSPKIHash: artifact.offer.tlsSPKISHA256)
                let configuration = URLSessionConfiguration.ephemeral
                configuration.timeoutIntervalForRequest = 20
                configuration.waitsForConnectivity = true
                let session = URLSession(configuration: configuration, delegate: delegate, delegateQueue: nil)
                let task = session.webSocketTask(with: try webSocketURL(endpoint))
                task.resume()

                do {
                    try await task.send(.data(JSONEncoder.phoneLink.encode(hello)))
                    let proofData = try await receiveData(task)
                    try StrictJSON.validateNoDuplicateObjectKeys(proofData)
                    let serverProof = try JSONDecoder.phoneLink.decode(PairingServerProof.self, from: proofData)
                    guard serverProof.kind == "pairing.serverProof",
                          serverProof.sessionId == artifact.offer.sessionID else {
                        throw PairingClientError.invalidServerProof
                    }

                    let serverNonce = try Base64URL.decode(serverProof.serverNonce)
                    let signature = try Base64URL.decode(serverProof.signature)
                    guard serverNonce.count == 32, signature.count == 64 else {
                        throw PairingClientError.invalidServerProof
                    }

                    let signatureInput = PairingTranscript.serverSignatureInput(
                        rawOfferJSON: artifact.rawJSON,
                        clientDeviceID: deviceID.uuidString.lowercased(),
                        clientIdentityPublicKey: identity.publicKey,
                        clientNonce: clientNonce,
                        serverNonce: serverNonce
                    )
                    let publicKey = try Curve25519.Signing.PublicKey(
                        rawRepresentation: artifact.offer.serverIdentityKey
                    )
                    guard publicKey.isValidSignature(signature, for: signatureInput) else {
                        throw PairingClientError.invalidServerSignature
                    }

                    let comparisonDigest = PairingTranscript.comparisonDigest(
                        serverSignatureInput: signatureInput,
                        serverSignature: signature
                    )
                    let phrase = try ComparisonPhrase.create(from: comparisonDigest)
                    return PendingPairingConnection(
                        sessionID: artifact.offer.sessionID,
                        comparisonPhrase: phrase,
                        task: task,
                        session: session,
                        delegate: delegate,
                        trustedPeerStore: trustedPeerStore,
                        serverIdentityPublicKey: artifact.offer.serverIdentityKey,
                        tlsSPKISHA256: artifact.offer.tlsSPKISHA256,
                        endpoints: artifact.offer.endpoints
                    )
                } catch {
                    task.cancel(with: .goingAway, reason: nil)
                    session.invalidateAndCancel()
                    throw error
                }
            } catch {
                lastError = error
            }
        }

        throw lastError
    }

    private func webSocketURL(_ endpoint: PairingEndpoint) throws -> URL {
        var components = URLComponents()
        components.scheme = "wss"
        components.host = endpoint.host
        components.port = Int(endpoint.port)
        components.path = "/pair/v1"
        guard let url = components.url else { throw PairingClientError.invalidEndpoint }
        return url
    }

    private func randomData(count: Int) throws -> Data {
        var bytes = [UInt8](repeating: 0, count: count)
        let status = bytes.withUnsafeMutableBytes { buffer in
            SecRandomCopyBytes(kSecRandomDefault, buffer.count, buffer.baseAddress!)
        }
        guard status == errSecSuccess else { throw PairingClientError.randomUnavailable }
        return Data(bytes)
    }

    fileprivate static func receiveData(_ task: URLSessionWebSocketTask) async throws -> Data {
        switch try await task.receive() {
        case .data(let data):
            return data
        case .string(let string):
            guard let data = string.data(using: .utf8) else {
                throw PairingClientError.invalidServerMessage
            }
            return data
        @unknown default:
            throw PairingClientError.invalidServerMessage
        }
    }
}

@MainActor
final class PendingPairingConnection {
    let sessionID: UUID
    let comparisonPhrase: String

    private let task: URLSessionWebSocketTask
    private let session: URLSession
    private let delegate: PinnedTLSDelegate
    private let trustedPeerStore: TrustedWindowsPeerStore
    private let serverIdentityPublicKey: Data
    private let tlsSPKISHA256: Data
    private let endpoints: [PairingEndpoint]
    private var isCompleted = false

    fileprivate init(
        sessionID: UUID,
        comparisonPhrase: String,
        task: URLSessionWebSocketTask,
        session: URLSession,
        delegate: PinnedTLSDelegate,
        trustedPeerStore: TrustedWindowsPeerStore,
        serverIdentityPublicKey: Data,
        tlsSPKISHA256: Data,
        endpoints: [PairingEndpoint]
    ) {
        self.sessionID = sessionID
        self.comparisonPhrase = comparisonPhrase
        self.task = task
        self.session = session
        self.delegate = delegate
        self.trustedPeerStore = trustedPeerStore
        self.serverIdentityPublicKey = serverIdentityPublicKey
        self.tlsSPKISHA256 = tlsSPKISHA256
        self.endpoints = endpoints
    }

    func confirm(_ approved: Bool) async throws -> Bool {
        guard !isCompleted else { throw PairingClientError.alreadyCompleted }
        isCompleted = true
        defer { session.finishTasksAndInvalidate() }

        let confirmation = PairingConfirmation(
            kind: "pairing.confirm",
            sessionId: sessionID,
            approved: approved
        )
        try await task.send(.data(JSONEncoder.phoneLink.encode(confirmation)))
        let completeData = try await PairingClient.receiveData(task)
        try StrictJSON.validateNoDuplicateObjectKeys(completeData)
        let complete = try JSONDecoder.phoneLink.decode(PairingComplete.self, from: completeData)
        guard complete.kind == "pairing.complete", complete.sessionId == sessionID else {
            throw PairingClientError.invalidServerMessage
        }
        if complete.paired {
            try trustedPeerStore.save(
                TrustedWindowsPeer(
                    deviceID: complete.serverDeviceId,
                    identityPublicKey: serverIdentityPublicKey,
                    tlsSPKISHA256: tlsSPKISHA256,
                    endpoints: endpoints,
                    pairedAt: Date()
                )
            )
        }
        return complete.paired
    }

    func cancel() {
        guard !isCompleted else { return }
        isCompleted = true
        task.cancel(with: .goingAway, reason: nil)
        session.invalidateAndCancel()
    }
}

private struct PairingClientHello: Codable {
    let kind: String
    let sessionId: UUID
    let clientDeviceId: UUID
    let clientDisplayName: String
    let clientIdentityPublicKey: String
    let clientNonce: String
    let proof: String
    let appVersion: String
}

private struct PairingServerProof: Codable {
    let kind: String
    let sessionId: UUID
    let serverNonce: String
    let signature: String
}

private struct PairingConfirmation: Codable {
    let kind: String
    let sessionId: UUID
    let approved: Bool
}

private struct PairingComplete: Codable {
    let kind: String
    let sessionId: UUID
    let paired: Bool
    let serverDeviceId: UUID
    let errorCode: String?
}

private extension JSONEncoder {
    static let phoneLink: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        return encoder
    }()
}

private extension JSONDecoder {
    static let phoneLink = JSONDecoder()
}

fileprivate final class PinnedTLSDelegate: NSObject, URLSessionDelegate, @unchecked Sendable {
    private let expectedSPKIHash: Data

    init(expectedSPKIHash: Data) {
        self.expectedSPKIHash = expectedSPKIHash
    }

    func urlSession(
        _ session: URLSession,
        didReceive challenge: URLAuthenticationChallenge,
        completionHandler: @escaping @Sendable (URLSession.AuthChallengeDisposition, URLCredential?) -> Void
    ) {
        guard challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              let trust = challenge.protectionSpace.serverTrust,
              let key = SecTrustCopyKey(trust),
              let rawKey = SecKeyCopyExternalRepresentation(key, nil) as Data?,
              let spki = p256SubjectPublicKeyInfo(rawKey),
              Data(SHA256.hash(data: spki)) == expectedSPKIHash else {
            completionHandler(.cancelAuthenticationChallenge, nil)
            return
        }

        completionHandler(.useCredential, URLCredential(trust: trust))
    }

    private func p256SubjectPublicKeyInfo(_ x963Key: Data) -> Data? {
        guard x963Key.count == 65, x963Key.first == 0x04 else { return nil }
        let prefix = Data([
            0x30, 0x59,
            0x30, 0x13,
            0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01,
            0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07,
            0x03, 0x42, 0x00,
        ])
        return prefix + x963Key
    }
}

enum PairingClientError: LocalizedError {
    case noReachableEndpoint
    case invalidEndpoint
    case invalidServerMessage
    case invalidServerProof
    case invalidServerSignature
    case alreadyCompleted
    case randomUnavailable

    var errorDescription: String? {
        switch self {
        case .noReachableEndpoint: "The Windows PC could not be reached on the local network."
        case .invalidEndpoint: "The Windows pairing endpoint is invalid."
        case .invalidServerMessage: "Windows returned an invalid pairing message."
        case .invalidServerProof: "Windows returned an invalid pairing proof."
        case .invalidServerSignature: "The Windows identity did not match the QR code. Pairing was stopped."
        case .alreadyCompleted: "This pairing attempt is already complete."
        case .randomUnavailable: "The iPhone cryptographic random generator is unavailable."
        }
    }
}
