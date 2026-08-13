import CryptoKit
import Foundation
import Security

struct ConnectedWindowsSession: Equatable {
    let peerDeviceID: UUID
    let sessionID: UUID
    let capabilities: [String]
    let endpoint: PairingEndpoint
}

@MainActor
final class DeviceSessionClient {
    private struct ResponseWaiter {
        let expectedKind: String
        let continuation: CheckedContinuation<Data, any Error>
    }

    private let identityStore: DeviceIdentityStore
    private var webSocket: URLSessionWebSocketTask?
    private var urlSession: URLSession?
    private var receiveTask: Task<Void, Never>?
    private var responseWaiters: [UUID: ResponseWaiter] = [:]
    private(set) var connection: ConnectedWindowsSession?
    var onCommandRequest: ((ProtocolEnvelope<CommandRequestPayload>) -> Void)?
    var onConnectionFailure: ((String) -> Void)?

    init(identityStore: DeviceIdentityStore = DeviceIdentityStore()) {
        self.identityStore = identityStore
    }

    func connect(to peer: TrustedWindowsPeer) async throws -> ConnectedWindowsSession {
        disconnect()
        var lastError: Error = DeviceSessionError.noReachableEndpoint

        let discovered = await PhoneLinkServiceBrowser().discoverEndpoints()
        var candidates: [PairingEndpoint] = []
        for endpoint in discovered + peer.endpoints where !candidates.contains(endpoint) {
            candidates.append(endpoint)
        }

        for endpoint in candidates {
            do {
                let connected = try await connect(to: endpoint, peer: peer)
                connection = connected
                startReceiveLoop()
                return connected
            } catch {
                lastError = error
            }
        }

        throw lastError
    }

    @discardableResult
    func send<Payload: Codable>(
        kind: String,
        payload: Payload,
        originSequence: Int64 = 0,
        requiresAck: Bool = false,
        correlationID: UUID? = nil
    ) async throws -> UUID {
        guard let webSocket, let connection else {
            throw DeviceSessionError.notConnected
        }

        let deviceID = try identityStore.loadOrCreateDeviceID()
        let messageID = UUID()
        let envelope = ProtocolEnvelope(
            protocolVersion: 1,
            messageId: messageID,
            correlationId: correlationID,
            sessionId: connection.sessionID,
            originDeviceId: deviceID,
            originSequence: originSequence,
            sentAt: PhoneLinkJSON.timestamp(),
            kind: kind,
            requiresAck: requiresAck,
            payload: payload
        )
        try await webSocket.send(.data(PhoneLinkJSON.encoder.encode(envelope)))
        return messageID
    }

    func sendAndAwaitSyncAck<Payload: Codable>(
        batchID: UUID,
        payload: Payload,
        originSequence: Int64
    ) async throws {
        let envelope: ProtocolEnvelope<SyncAckPayload> = try await sendAndAwaitResponse(
            kind: "sync.batch",
            payload: payload,
            originSequence: originSequence,
            expectedKind: "sync.ack"
        )
        guard envelope.kind == "sync.ack",
              envelope.payload.batchId == batchID,
              envelope.payload.highestContiguousSequence == originSequence else {
            throw DeviceSessionError.invalidAcknowledgement
        }
    }

    func sendPhoto(
        data: Data,
        displayName: String,
        mediaType: String,
        progress: @MainActor (Double) -> Void
    ) async throws {
        guard data.count <= 25 * 1024 * 1024 else {
            throw PhotoTransferError.tooLarge
        }
        guard let webSocket, connection != nil else {
            throw DeviceSessionError.notConnected
        }

        let attachmentID = UUID()
        let contentHash = Data(SHA256.hash(data: data))
        let chunkSize = 128 * 1024
        let offer = AttachmentOfferPayload(
            attachmentId: attachmentID,
            displayName: String(displayName.prefix(500)),
            mediaType: mediaType,
            byteCount: Int64(data.count),
            contentSha256: Base64URL.encode(contentHash),
            chunkSize: chunkSize
        )
        let acceptEnvelope: ProtocolEnvelope<AttachmentAcceptPayload> = try await sendAndAwaitResponse(
            kind: "attachment.offer",
            payload: offer,
            expectedKind: "attachment.accept"
        )
        guard acceptEnvelope.kind == "attachment.accept",
              acceptEnvelope.payload.attachmentId == attachmentID,
              acceptEnvelope.payload.accepted else {
            throw PhotoTransferError.rejected(acceptEnvelope.payload.errorCode)
        }

        var chunkIndex = acceptEnvelope.payload.nextChunkIndex
        var offset = Int(chunkIndex) * chunkSize
        while offset < data.count {
            let end = min(offset + chunkSize, data.count)
            let chunk = data.subdata(in: offset..<end)
            let frame = try AttachmentBinaryFrame.encode(
                attachmentID: attachmentID,
                chunkIndex: chunkIndex,
                payload: chunk
            )
            try await webSocket.send(.data(frame))
            offset = end
            chunkIndex += 1
            progress(Double(offset) / Double(data.count))
        }

        let completeEnvelope: ProtocolEnvelope<AttachmentCompletePayload> = try await sendAndAwaitResponse(
            kind: "attachment.complete",
            payload: AttachmentCompletePayload(
                attachmentId: attachmentID,
                verified: false,
                errorCode: nil
            ),
            expectedKind: "attachment.complete"
        )
        guard completeEnvelope.kind == "attachment.complete",
              completeEnvelope.payload.attachmentId == attachmentID,
              completeEnvelope.payload.verified else {
            throw PhotoTransferError.integrityFailed
        }
    }

    func sendCommandResult(
        for request: ProtocolEnvelope<CommandRequestPayload>,
        status: String,
        errorCode: String? = nil
    ) async throws {
        try await send(
            kind: "command.result",
            payload: CommandResultPayload(
                commandId: request.payload.commandId,
                status: status,
                errorCode: errorCode,
                finishedAt: PhoneLinkJSON.timestamp()
            ),
            correlationID: request.messageId
        )
    }

    func disconnect() {
        receiveTask?.cancel()
        receiveTask = nil
        failAllWaiters(with: DeviceSessionError.notConnected)
        webSocket?.cancel(with: .goingAway, reason: nil)
        urlSession?.invalidateAndCancel()
        webSocket = nil
        urlSession = nil
        connection = nil
    }

    private func connect(
        to endpoint: PairingEndpoint,
        peer: TrustedWindowsPeer
    ) async throws -> ConnectedWindowsSession {
        let identity = try identityStore.loadOrCreate()
        let clientDeviceID = try identityStore.loadOrCreateDeviceID()
        let clientNonce = try randomData(count: 32)
        let connectionID = UUID()
        let appVersion = Bundle.main.object(
            forInfoDictionaryKey: "CFBundleShortVersionString"
        ) as? String ?? "0"

        let delegate = SessionPinnedTLSDelegate(expectedSPKIHash: peer.tlsSPKISHA256)
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 20
        configuration.waitsForConnectivity = true
        let session = URLSession(configuration: configuration, delegate: delegate, delegateQueue: nil)
        let task = session.webSocketTask(with: try webSocketURL(endpoint))
        task.resume()

        do {
            let hello = SessionClientHello(
                kind: "session.clientHello",
                connectionId: connectionID,
                clientDeviceId: clientDeviceID,
                clientNonce: Base64URL.encode(clientNonce),
                protocolFloor: 1,
                protocolCeiling: 1,
                appVersion: appVersion
            )
            try await task.send(.data(PhoneLinkJSON.encoder.encode(hello)))

            let challengeData = try await receiveData(task)
            try StrictJSON.validateNoDuplicateObjectKeys(challengeData)
            let challenge = try PhoneLinkJSON.decoder.decode(
                SessionServerChallenge.self,
                from: challengeData
            )
            guard challenge.kind == "session.serverChallenge",
                  challenge.connectionId == connectionID,
                  challenge.serverDeviceId == peer.deviceID,
                  challenge.selectedProtocolVersion == 1 else {
                throw DeviceSessionError.invalidServerChallenge
            }

            let serverNonce = try Base64URL.decode(challenge.serverNonce)
            let serverSignature = try Base64URL.decode(challenge.signature)
            guard serverNonce.count == 32, serverSignature.count == 64 else {
                throw DeviceSessionError.invalidServerChallenge
            }

            let serverProofInput = SessionTranscript.serverProofInput(
                connectionID: connectionID,
                sessionID: challenge.sessionId,
                serverDeviceID: challenge.serverDeviceId,
                clientDeviceID: clientDeviceID,
                clientNonce: clientNonce,
                serverNonce: serverNonce,
                selectedProtocolVersion: challenge.selectedProtocolVersion,
                clientAppVersion: appVersion
            )
            let serverPublicKey = try Curve25519.Signing.PublicKey(
                rawRepresentation: peer.identityPublicKey
            )
            guard serverPublicKey.isValidSignature(serverSignature, for: serverProofInput) else {
                throw DeviceSessionError.serverIdentityMismatch
            }

            let clientProofInput = SessionTranscript.clientProofInput(
                serverProofInput: serverProofInput,
                serverSignature: serverSignature
            )
            let clientSignature = try identity.privateKey.signature(for: clientProofInput)
            let proof = SessionClientProof(
                kind: "session.clientProof",
                connectionId: connectionID,
                sessionId: challenge.sessionId,
                signature: Base64URL.encode(clientSignature)
            )
            try await task.send(.data(PhoneLinkJSON.encoder.encode(proof)))

            let readyData = try await receiveData(task)
            try StrictJSON.validateNoDuplicateObjectKeys(readyData)
            let ready = try PhoneLinkJSON.decoder.decode(SessionReady.self, from: readyData)
            guard ready.kind == "session.ready",
                  ready.sessionId == challenge.sessionId,
                  ready.serverDeviceId == peer.deviceID,
                  ready.protocolVersion == 1 else {
                throw DeviceSessionError.invalidReadyMessage
            }

            webSocket = task
            urlSession = session
            return ConnectedWindowsSession(
                peerDeviceID: peer.deviceID,
                sessionID: ready.sessionId,
                capabilities: ready.capabilities,
                endpoint: endpoint
            )
        } catch {
            task.cancel(with: .goingAway, reason: nil)
            session.invalidateAndCancel()
            throw error
        }
    }

    private func sendAndAwaitResponse<Request: Codable, Response: Codable>(
        kind: String,
        payload: Request,
        originSequence: Int64 = 0,
        expectedKind: String
    ) async throws -> ProtocolEnvelope<Response> {
        guard let webSocket, let connection else {
            throw DeviceSessionError.notConnected
        }

        let messageID = UUID()
        let deviceID = try identityStore.loadOrCreateDeviceID()
        let envelope = ProtocolEnvelope(
            protocolVersion: 1,
            messageId: messageID,
            correlationId: nil,
            sessionId: connection.sessionID,
            originDeviceId: deviceID,
            originSequence: originSequence,
            sentAt: PhoneLinkJSON.timestamp(),
            kind: kind,
            requiresAck: true,
            payload: payload
        )
        let requestData = try PhoneLinkJSON.encoder.encode(envelope)
        let responseData: Data = try await withCheckedThrowingContinuation { continuation in
            responseWaiters[messageID] = ResponseWaiter(
                expectedKind: expectedKind,
                continuation: continuation
            )
            Task { @MainActor [weak self] in
                guard let self else {
                    continuation.resume(throwing: DeviceSessionError.notConnected)
                    return
                }
                do {
                    try await webSocket.send(.data(requestData))
                } catch {
                    self.failWaiter(messageID, with: error)
                }
            }
        }

        try StrictJSON.validateNoDuplicateObjectKeys(responseData)
        let response = try PhoneLinkJSON.decoder.decode(
            ProtocolEnvelope<Response>.self,
            from: responseData
        )
        guard response.kind == expectedKind,
              response.correlationId == messageID,
              response.sessionId == connection.sessionID,
              response.originDeviceId == connection.peerDeviceID else {
            throw DeviceSessionError.invalidAcknowledgement
        }
        return response
    }

    private func startReceiveLoop() {
        guard let webSocket else { return }
        receiveTask?.cancel()
        receiveTask = Task { [weak self] in
            guard let self else { return }
            await self.receiveLoop(webSocket)
        }
    }

    private func receiveLoop(_ task: URLSessionWebSocketTask) async {
        while !Task.isCancelled, webSocket === task {
            do {
                let data = try await receiveData(task)
                try StrictJSON.validateNoDuplicateObjectKeys(data)
                let header = try PhoneLinkJSON.decoder.decode(
                    ProtocolEnvelopeHeader.self,
                    from: data
                )
                guard let connection,
                      header.protocolVersion == 1,
                      header.sessionId == connection.sessionID,
                      header.originDeviceId == connection.peerDeviceID else {
                    throw DeviceSessionError.invalidServerMessage
                }

                if let correlationID = header.correlationId,
                   let waiter = responseWaiters.removeValue(forKey: correlationID) {
                    guard header.kind == waiter.expectedKind else {
                        waiter.continuation.resume(
                            throwing: DeviceSessionError.invalidAcknowledgement
                        )
                        continue
                    }
                    waiter.continuation.resume(returning: data)
                    continue
                }

                if header.kind == "command.request" {
                    let request = try PhoneLinkJSON.decoder.decode(
                        ProtocolEnvelope<CommandRequestPayload>.self,
                        from: data
                    )
                    guard request.requiresAck,
                          request.correlationId == nil else {
                        throw DeviceSessionError.invalidServerMessage
                    }
                    onCommandRequest?(request)
                } else if header.kind == "ping" {
                    try await send(
                        kind: "pong",
                        payload: EmptyPayload(),
                        correlationID: header.messageId
                    )
                }
            } catch is CancellationError {
                return
            } catch {
                guard webSocket === task else { return }
                failAllWaiters(with: error)
                task.cancel(with: .goingAway, reason: nil)
                urlSession?.invalidateAndCancel()
                webSocket = nil
                urlSession = nil
                connection = nil
                onConnectionFailure?(error.localizedDescription)
                return
            }
        }
    }

    private func failWaiter(_ messageID: UUID, with error: any Error) {
        responseWaiters.removeValue(forKey: messageID)?.continuation.resume(throwing: error)
    }

    private func failAllWaiters(with error: any Error) {
        let waiters = responseWaiters.values
        responseWaiters.removeAll()
        for waiter in waiters {
            waiter.continuation.resume(throwing: error)
        }
    }

    private func webSocketURL(_ endpoint: PairingEndpoint) throws -> URL {
        var components = URLComponents()
        components.scheme = "wss"
        components.host = endpoint.host
        components.port = Int(endpoint.port)
        components.path = "/sync/v1"
        guard let url = components.url else { throw DeviceSessionError.invalidEndpoint }
        return url
    }

    private func randomData(count: Int) throws -> Data {
        var bytes = [UInt8](repeating: 0, count: count)
        let status = bytes.withUnsafeMutableBytes { buffer in
            SecRandomCopyBytes(kSecRandomDefault, buffer.count, buffer.baseAddress!)
        }
        guard status == errSecSuccess else { throw DeviceSessionError.randomUnavailable }
        return Data(bytes)
    }

    private func receiveData(_ task: URLSessionWebSocketTask) async throws -> Data {
        switch try await task.receive() {
        case .data(let data): return data
        case .string(let string):
            guard let data = string.data(using: .utf8) else {
                throw DeviceSessionError.invalidServerMessage
            }
            return data
        @unknown default: throw DeviceSessionError.invalidServerMessage
        }
    }

}

private final class SessionPinnedTLSDelegate: NSObject, URLSessionDelegate, @unchecked Sendable {
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
              rawKey.count == 65,
              rawKey.first == 0x04 else {
            completionHandler(.cancelAuthenticationChallenge, nil)
            return
        }

        let prefix = Data([
            0x30, 0x59, 0x30, 0x13,
            0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01,
            0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07,
            0x03, 0x42, 0x00,
        ])
        guard Data(SHA256.hash(data: prefix + rawKey)) == expectedSPKIHash else {
            completionHandler(.cancelAuthenticationChallenge, nil)
            return
        }

        completionHandler(.useCredential, URLCredential(trust: trust))
    }
}

enum DeviceSessionError: LocalizedError {
    case noReachableEndpoint
    case invalidEndpoint
    case invalidServerMessage
    case invalidServerChallenge
    case invalidReadyMessage
    case serverIdentityMismatch
    case randomUnavailable
    case notConnected
    case invalidAcknowledgement

    var errorDescription: String? {
        switch self {
        case .noReachableEndpoint: "The trusted Windows PC could not be reached on the local network."
        case .invalidEndpoint: "The stored Windows endpoint is invalid."
        case .invalidServerMessage: "Windows returned an invalid session message."
        case .invalidServerChallenge: "Windows returned an invalid authentication challenge."
        case .invalidReadyMessage: "Windows did not finish the authenticated session correctly."
        case .serverIdentityMismatch: "The Windows identity changed. Re-pair before sending any data."
        case .randomUnavailable: "The iPhone cryptographic random generator is unavailable."
        case .notConnected: "Connect to a trusted Windows PC first."
        case .invalidAcknowledgement: "Windows returned an invalid synchronization acknowledgement."
        }
    }
}

enum PhotoTransferError: LocalizedError {
    case tooLarge
    case invalidChunk
    case rejected(String?)
    case integrityFailed

    var errorDescription: String? {
        switch self {
        case .tooLarge: "Each selected photo must be 25 MB or smaller."
        case .invalidChunk: "The photo chunk could not be encoded."
        case .rejected(let code): "Windows rejected the photo transfer (\(code ?? "policy"))."
        case .integrityFailed: "Windows could not verify the imported photo."
        }
    }
}
