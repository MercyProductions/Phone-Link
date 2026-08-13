import Foundation
import UIKit

@MainActor
final class CompanionSession: ObservableObject {
    enum StartupState: Equatable {
        case starting
        case ready(DeviceIdentitySummary)
        case failed(String)
    }

    enum ConnectionState: Equatable {
        case disconnected
        case connecting(UUID)
        case connected(ConnectedWindowsSession)
        case failed(String)
    }

    @Published private(set) var startupState: StartupState = .starting
    @Published private(set) var trustedPeers: [TrustedWindowsPeer] = []
    @Published private(set) var connectionState: ConnectionState = .disconnected
    @Published private(set) var pendingDialRequest: PendingDialRequest?

    private let identityStore: DeviceIdentityStore
    private let trustedPeerStore: TrustedWindowsPeerStore
    private let deviceSessionClient: DeviceSessionClient
    private var hasStarted = false

    init(
        identityStore: DeviceIdentityStore = DeviceIdentityStore(),
        trustedPeerStore: TrustedWindowsPeerStore = TrustedWindowsPeerStore()
    ) {
        self.identityStore = identityStore
        self.trustedPeerStore = trustedPeerStore
        self.deviceSessionClient = DeviceSessionClient(identityStore: identityStore)
        self.deviceSessionClient.onCommandRequest = { [weak self] request in
            self?.handleCommandRequest(request)
        }
        self.deviceSessionClient.onConnectionFailure = { [weak self] message in
            self?.pendingDialRequest = nil
            self?.connectionState = .failed(message)
        }
    }

    func start() {
        guard !hasStarted else { return }
        hasStarted = true

        do {
            let identity = try identityStore.loadOrCreate()
            startupState = .ready(
                DeviceIdentitySummary(
                    deviceName: UIDevice.current.name,
                    publicKey: identity.publicKey,
                    fingerprint: IdentityFingerprint.display(identity.publicKey)
                )
            )
            refreshTrustedPeers()
            if let peer = trustedPeers.first {
                Task { await connect(to: peer) }
            }
        } catch {
            startupState = .failed("The device identity could not be protected in Keychain. \(error.localizedDescription)")
        }
    }

    func refreshTrustedPeers() {
        do {
            trustedPeers = try trustedPeerStore.loadAll()
                .sorted { $0.pairedAt > $1.pairedAt }
        } catch {
            connectionState = .failed("Trusted PC records could not be read. \(error.localizedDescription)")
        }
    }

    func connect(to peer: TrustedWindowsPeer) async {
        connectionState = .connecting(peer.deviceID)
        do {
            connectionState = .connected(try await deviceSessionClient.connect(to: peer))
        } catch {
            connectionState = .failed(error.localizedDescription)
        }
    }

    func disconnect() {
        deviceSessionClient.disconnect()
        pendingDialRequest = nil
        connectionState = .disconnected
    }

    func resolveDialRequest(approved: Bool) async {
        guard let pending = pendingDialRequest else { return }
        pendingDialRequest = nil

        guard pending.expiresAt > Date() else {
            try? await deviceSessionClient.sendCommandResult(
                for: pending.envelope,
                status: "expired",
                errorCode: "command.expired"
            )
            return
        }

        if !approved {
            try? await deviceSessionClient.sendCommandResult(
                for: pending.envelope,
                status: "declined",
                errorCode: "user.declined"
            )
            return
        }

        let allowed = CharacterSet(charactersIn: "+0123456789")
        guard let encoded = pending.phoneNumber.addingPercentEncoding(
            withAllowedCharacters: allowed
        ), let url = URL(string: "tel:\(encoded)") else {
            try? await deviceSessionClient.sendCommandResult(
                for: pending.envelope,
                status: "failed",
                errorCode: "call.invalid-number"
            )
            return
        }

        let launched = await UIApplication.shared.open(url)
        try? await deviceSessionClient.sendCommandResult(
            for: pending.envelope,
            status: launched ? "launched" : "failed",
            errorCode: launched ? nil : "call.system-rejected"
        )
    }

    func send<Payload: Codable>(
        kind: String,
        payload: Payload,
        originSequence: Int64 = 0,
        requiresAck: Bool = false
    ) async throws {
        try await deviceSessionClient.send(
            kind: kind,
            payload: payload,
            originSequence: originSequence,
            requiresAck: requiresAck
        )
    }

    func sendAndAwaitSyncAck<Payload: Codable>(
        batchID: UUID,
        payload: Payload,
        originSequence: Int64
    ) async throws {
        try await deviceSessionClient.sendAndAwaitSyncAck(
            batchID: batchID,
            payload: payload,
            originSequence: originSequence
        )
    }

    func sendPhoto(
        data: Data,
        displayName: String,
        mediaType: String,
        progress: @MainActor (Double) -> Void
    ) async throws {
        try await deviceSessionClient.sendPhoto(
            data: data,
            displayName: displayName,
            mediaType: mediaType,
            progress: progress
        )
    }

    private func handleCommandRequest(
        _ request: ProtocolEnvelope<CommandRequestPayload>
    ) {
        guard request.payload.name == "call.start" else {
            Task {
                try? await deviceSessionClient.sendCommandResult(
                    for: request,
                    status: "unsupported",
                    errorCode: "command.unsupported"
                )
            }
            return
        }
        guard request.payload.requiresUserConfirmation,
              let phoneNumber = request.payload.arguments["phoneNumber"],
              isSafeDialString(phoneNumber),
              let expiresAt = parseTimestamp(request.payload.expiresAt),
              expiresAt > Date() else {
            Task {
                try? await deviceSessionClient.sendCommandResult(
                    for: request,
                    status: "rejected",
                    errorCode: "command.invalid-or-expired"
                )
            }
            return
        }
        guard pendingDialRequest == nil else {
            Task {
                try? await deviceSessionClient.sendCommandResult(
                    for: request,
                    status: "rejected",
                    errorCode: "command.busy"
                )
            }
            return
        }

        pendingDialRequest = PendingDialRequest(
            envelope: request,
            phoneNumber: phoneNumber,
            expiresAt: expiresAt
        )
    }

    private func isSafeDialString(_ value: String) -> Bool {
        guard value.count <= 32 else { return false }
        let allowed = CharacterSet(charactersIn: "+0123456789*#")
        guard value.unicodeScalars.allSatisfy({ allowed.contains($0) }),
              value.filter({ $0.isNumber }).count >= 3 else { return false }
        return !value.dropFirst().contains("+")
    }

    private func parseTimestamp(_ value: String) -> Date? {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.date(from: value) ?? ISO8601DateFormatter().date(from: value)
    }
}

struct DeviceIdentitySummary: Equatable {
    let deviceName: String
    let publicKey: Data
    let fingerprint: String
}

struct PendingDialRequest: Identifiable {
    var id: UUID { envelope.payload.commandId }

    let envelope: ProtocolEnvelope<CommandRequestPayload>
    let phoneNumber: String
    let expiresAt: Date
}
