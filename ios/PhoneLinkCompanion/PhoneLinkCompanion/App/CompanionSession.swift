import Foundation
import UIKit
import Network

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
    @Published private(set) var reconnectStatus: String?

    var connectedSession: ConnectedWindowsSession? {
        if case .connected(let connection) = connectionState { return connection }
        return nil
    }
    var preferredPeer: TrustedWindowsPeer? {
        trustedPeers.first(where: { $0.deviceID == selectedPeerID }) ?? trustedPeers.first
    }

    private let identityStore: DeviceIdentityStore
    private let trustedPeerStore: TrustedWindowsPeerStore
    private let deviceSessionClient: DeviceSessionClient
    private var hasStarted = false
    private var isForeground = false
    private var networkAvailable = true
    private var selectedPeerID: UUID?
    private var connectionAttemptID = UUID()
    private var reconnectAttempt = 0
    private var reconnectTask: Task<Void, Never>?
    private var pathMonitor: NWPathMonitor?
    private var pathMonitorID = UUID()
    private let defaults: UserDefaults
    private var manuallyDisconnected: Bool

    init(
        identityStore: DeviceIdentityStore = DeviceIdentityStore(),
        trustedPeerStore: TrustedWindowsPeerStore = TrustedWindowsPeerStore(),
        defaults: UserDefaults = .standard
    ) {
        self.identityStore = identityStore
        self.trustedPeerStore = trustedPeerStore
        self.defaults = defaults
        self.manuallyDisconnected = defaults.bool(forKey: "connection.manually-disconnected.v1")
        self.selectedPeerID = defaults.string(forKey: "connection.selected-peer.v1").flatMap(UUID.init(uuidString:))
        self.deviceSessionClient = DeviceSessionClient(identityStore: identityStore)
        self.deviceSessionClient.onCommandRequest = { [weak self] request in
            self?.handleCommandRequest(request)
        }
        self.deviceSessionClient.onConnectionFailure = { [weak self] message in
            self?.pendingDialRequest = nil
            self?.connectionState = .failed(message)
            self?.scheduleReconnect()
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
            scheduleReconnect(immediate: true)
        } catch {
            startupState = .failed("The device identity could not be protected in Keychain. \(error.localizedDescription)")
        }
    }

    func refreshTrustedPeers() {
        do {
            trustedPeers = try trustedPeerStore.loadAll()
                .sorted { $0.pairedAt > $1.pairedAt }
            if let selectedPeerID, !trustedPeers.contains(where: { $0.deviceID == selectedPeerID }) {
                disconnect()
                self.selectedPeerID = nil
            }
            if selectedPeerID == nil { selectedPeerID = trustedPeers.first?.deviceID }
            scheduleReconnect(immediate: true)
        } catch {
            trustedPeers = []
            stopConnection()
            connectionState = .failed("Trusted PC records could not be read. \(error.localizedDescription)")
        }
    }

    func connect(to peer: TrustedWindowsPeer) async {
        guard trustedPeers.contains(where: { $0.deviceID == peer.deviceID }) else { return }
        manuallyDisconnected = false
        defaults.set(false, forKey: "connection.manually-disconnected.v1")
        selectedPeerID = peer.deviceID
        defaults.set(peer.deviceID.uuidString, forKey: "connection.selected-peer.v1")
        reconnectAttempt = 0
        await performConnection(to: peer)
    }

    private func performConnection(to peer: TrustedWindowsPeer) async {
        guard isForeground, networkAvailable, !manuallyDisconnected, trustedPeers.contains(peer) else { return }
        reconnectTask?.cancel()
        reconnectTask = nil
        connectionAttemptID = UUID()
        let attemptID = connectionAttemptID
        reconnectStatus = nil
        connectionState = .connecting(peer.deviceID)
        do {
            let connected = try await deviceSessionClient.connect(to: peer)
            guard attemptID == connectionAttemptID, isForeground, !manuallyDisconnected,
                  trustedPeers.contains(where: { $0.deviceID == peer.deviceID }) else { return }
            reconnectAttempt = 0
            connectionState = .connected(connected)
        } catch {
            guard attemptID == connectionAttemptID, !Task.isCancelled else { return }
            connectionState = .failed(error.localizedDescription)
            scheduleReconnect()
        }
    }

    func disconnect() {
        manuallyDisconnected = true
        defaults.set(true, forKey: "connection.manually-disconnected.v1")
        stopConnection()
        reconnectStatus = "Automatic reconnection is paused until you choose Connect."
    }

    private func stopConnection() {
        connectionAttemptID = UUID()
        reconnectTask?.cancel()
        reconnectTask = nil
        deviceSessionClient.disconnect()
        pendingDialRequest = nil
        connectionState = .disconnected
    }

    func setForegroundActive(_ active: Bool) {
        guard isForeground != active else { return }
        isForeground = active
        if active {
            let monitor = NWPathMonitor()
            pathMonitorID = UUID()
            let monitorID = pathMonitorID
            pathMonitor = monitor
            monitor.pathUpdateHandler = { [weak self] path in
                let available = path.status == .satisfied
                Task { @MainActor in self?.networkChanged(available: available, monitorID: monitorID) }
            }
            monitor.start(queue: .main)
            refreshTrustedPeers()
            scheduleReconnect(immediate: true)
        } else {
            pathMonitor?.cancel()
            pathMonitor = nil
            pathMonitorID = UUID()
            stopConnection()
            reconnectStatus = "Reconnection resumes when Phone Link returns to the foreground."
        }
    }

    private func networkChanged(available: Bool, monitorID: UUID) {
        guard isForeground, pathMonitorID == monitorID else { return }
        let wasAvailable = networkAvailable
        networkAvailable = available
        if !available {
            stopConnection()
            reconnectStatus = "Waiting for a local network."
        } else if !wasAvailable || connectedSession == nil {
            scheduleReconnect(immediate: true)
        }
    }

    private func scheduleReconnect(immediate: Bool = false) {
        guard hasStarted, isForeground, networkAvailable, !manuallyDisconnected,
              connectedSession == nil, reconnectTask == nil,
              let peer = trustedPeers.first(where: { $0.deviceID == selectedPeerID }) else { return }
        if case .connecting = connectionState { return }
        let delay = immediate ? 0 : min(60, 1 << min(reconnectAttempt, 6))
        reconnectAttempt += 1
        reconnectStatus = delay == 0 ? "Reconnecting…" : "Reconnecting in \(delay) seconds…"
        reconnectTask = Task { [weak self] in
            do { try await Task.sleep(for: .seconds(delay)) }
            catch { return }
            guard let self, !Task.isCancelled else { return }
            self.reconnectTask = nil
            await self.performConnection(to: peer)
        }
    }

    func revokePeer(_ deviceID: UUID) {
        do {
            try trustedPeerStore.delete(deviceID)
            if selectedPeerID == deviceID || connectedSession?.peerDeviceID == deviceID {
                disconnect()
                selectedPeerID = nil
                defaults.removeObject(forKey: "connection.selected-peer.v1")
            }
            refreshTrustedPeers()
        } catch {
            connectionState = .failed("The trusted PC could not be revoked. \(error.localizedDescription)")
        }
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
