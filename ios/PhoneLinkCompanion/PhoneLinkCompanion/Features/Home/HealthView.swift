import SwiftUI
import UIKit
import Contacts
import Photos
import AVFoundation
import UserNotifications

struct CompanionHealthSnapshot: Codable, Equatable, Sendable {
    let schemaVersion: Int
    let capturedAt: String
    let batteryPercent: Int?
    let batteryState: String
    let thermalState: String
    let lowPowerMode: Bool
    let contactsPermission: String
    let photosPermission: String
    let cameraPermission: String
    let notificationPermission: String

    // Encode unavailable battery explicitly as null, matching the required wire field.
    enum CodingKeys: String, CodingKey {
        case schemaVersion, capturedAt, batteryPercent, batteryState, thermalState, lowPowerMode
        case contactsPermission, photosPermission, cameraPermission, notificationPermission
    }
    func encode(to encoder: any Encoder) throws {
        var values = encoder.container(keyedBy: CodingKeys.self)
        try values.encode(schemaVersion, forKey: .schemaVersion)
        try values.encode(capturedAt, forKey: .capturedAt)
        try values.encode(batteryPercent, forKey: .batteryPercent)
        try values.encode(batteryState, forKey: .batteryState)
        try values.encode(thermalState, forKey: .thermalState)
        try values.encode(lowPowerMode, forKey: .lowPowerMode)
        try values.encode(contactsPermission, forKey: .contactsPermission)
        try values.encode(photosPermission, forKey: .photosPermission)
        try values.encode(cameraPermission, forKey: .cameraPermission)
        try values.encode(notificationPermission, forKey: .notificationPermission)
    }

    static func percentage(_ value: Float) -> Int? {
        guard value.isFinite, (0...1).contains(value) else { return nil }
        return Int((value * 100).rounded())
    }

    var summary: String {
        "Captured: \(capturedAt)\nCharge: \(batteryPercent.map { "\($0)%" } ?? "unknown") · \(batteryState)\n" +
        "Thermal pressure: \(thermalState) (not temperature)\nLow-power mode: \(lowPowerMode ? "on" : "off")\n" +
        "Companion permissions — contacts: \(contactsPermission); photos: \(photosPermission); camera: \(cameraPermission); notifications: \(notificationPermission)."
    }
}

@MainActor
enum CompanionHealthProbe {
    static func capture() async -> CompanionHealthSnapshot {
        let settings = await UNUserNotificationCenter.current().notificationSettings()
        let device = UIDevice.current
        let previousMonitoring = device.isBatteryMonitoringEnabled
        device.isBatteryMonitoringEnabled = true
        defer { device.isBatteryMonitoringEnabled = previousMonitoring }
        let battery: String
        switch device.batteryState {
        case .unknown: battery = "unknown"
        case .unplugged: battery = "unplugged"
        case .charging: battery = "charging"
        case .full: battery = "full"
        @unknown default: battery = "unknown"
        }
        let thermal: String
        switch ProcessInfo.processInfo.thermalState {
        case .nominal: thermal = "nominal"
        case .fair: thermal = "fair"
        case .serious: thermal = "serious"
        case .critical: thermal = "critical"
        @unknown default: thermal = "unknown"
        }
        let contacts: String
        switch CNContactStore.authorizationStatus(for: .contacts) {
        case .notDetermined: contacts = "not-determined"
        case .restricted: contacts = "restricted"
        case .denied: contacts = "denied"
        case .authorized: contacts = "authorized"
        case .limited: contacts = "limited"
        @unknown default: contacts = "unknown"
        }
        let photos: String
        switch PHPhotoLibrary.authorizationStatus(for: .readWrite) {
        case .notDetermined: photos = "not-determined"
        case .restricted: photos = "restricted"
        case .denied: photos = "denied"
        case .authorized: photos = "authorized"
        case .limited: photos = "limited"
        @unknown default: photos = "unknown"
        }
        let camera: String
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .notDetermined: camera = "not-determined"
        case .restricted: camera = "restricted"
        case .denied: camera = "denied"
        case .authorized: camera = "authorized"
        @unknown default: camera = "unknown"
        }
        let notifications: String
        switch settings.authorizationStatus {
        case .notDetermined: notifications = "not-determined"
        case .denied: notifications = "denied"
        case .authorized: notifications = "authorized"
        case .provisional: notifications = "provisional"
        case .ephemeral: notifications = "ephemeral"
        @unknown default: notifications = "unknown"
        }
        return CompanionHealthSnapshot(schemaVersion: 1, capturedAt: PhoneLinkJSON.timestamp(),
            batteryPercent: CompanionHealthSnapshot.percentage(device.batteryLevel), batteryState: battery,
            thermalState: thermal, lowPowerMode: ProcessInfo.processInfo.isLowPowerModeEnabled,
            contactsPermission: contacts, photosPermission: photos, cameraPermission: camera,
            notificationPermission: notifications)
    }

    static func localStorageText() -> String {
        do {
            let values = try URL(fileURLWithPath: NSHomeDirectory()).resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey])
            guard let bytes = values.volumeAvailableCapacityForImportantUsage, bytes >= 0 else { return "Storage estimate unavailable." }
            return "Estimated capacity available for important usage: \(ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)). Not a guarantee of writable space."
        } catch { return "Storage estimate unavailable; no storage fault is inferred." }
    }
}

struct HealthView: View {
    @EnvironmentObject private var session: CompanionSession
    @Environment(\.scenePhase) private var phase
    @State private var snapshot: CompanionHealthSnapshot?
    @State private var storageText = "Not checked."
    @State private var status = "No checks have run."
    @State private var busy = false
    @State private var epoch = UUID()
    @State private var shareIntent: HealthShareIntent?
    @State private var confirming = false

    var body: some View {
        NavigationStack {
            Form {
                Section("Interactive checks") {
                    NavigationLink("Own runtime image and ASLR") { RuntimeImageView() }
                    NavigationLink("Touch, display, sound and haptics") { GuidedChecksView() }
                    Text("Run checks yourself on this iPhone. Observations stay local and do not certify hardware health.")
                        .font(.footnote).foregroundStyle(.secondary)
                }
                Section("Read-only phone checks") {
                    Text("Capture a one-time snapshot. No permission prompt, repair, reset or firmware change is performed. No background monitoring.")
                    Button("Capture snapshot") { Task { await capture() } }.disabled(busy)
                    if let snapshot { Text(snapshot.summary).textSelection(.enabled) }
                    Text("Charge is not battery health or maximum capacity. Thermal pressure is not a sensor temperature or a hardware-fault diagnosis.")
                        .font(.footnote).foregroundStyle(.secondary)
                }
                Section("Storage — stays on this iPhone") {
                    Text(storageText)
                    Text("Storage is never included in the Windows snapshot. This estimate is displayed only here.")
                        .font(.footnote).foregroundStyle(.secondary)
                }
                Section("Send reviewed snapshot to your PC") {
                    if let connection = session.connectedSession {
                        Text("Connected PC identity: \(connection.peerDeviceID.uuidString.lowercased())")
                            .font(.caption.monospaced()).textSelection(.enabled)
                    }
                    Button("Review sending to connected PC") {
                        guard let snapshot, let connection = session.connectedSession else { return }
                        shareIntent = HealthShareIntent(snapshot: snapshot, connection: connection)
                        confirming = true
                    }.disabled(busy || snapshot == nil || session.connectedSession == nil)
                    Text("Only the displayed charge, thermal, low-power and companion-permission fields are sent through the authenticated connection. No storage, contacts, photos, logs or device identifiers are in the payload. Windows keeps only the latest snapshot in memory for the session and displays receipts younger than five minutes; press Run connection checks there to display it. Lock or disconnect clears the inbox.")
                        .font(.footnote).foregroundStyle(.secondary)
                    Text(status).font(.footnote)
                }
            }
            .navigationTitle("Health checks")
            .alert("Send this snapshot?", isPresented: $confirming) {
                Button("Send to this PC") { Task { await sendReviewed() } }
                Button("Cancel", role: .cancel) { shareIntent = nil }
            } message: {
                Text("PC identity: \(shareIntent?.connection.peerDeviceID.uuidString.lowercased() ?? "unavailable")\n\n\(shareIntent?.snapshot.summary ?? "No snapshot")\n\nStorage stays on this iPhone. Send only to a PC you own.")
            }
            .onChange(of: phase) { _, newPhase in
                if newPhase != .active {
                    epoch = UUID(); snapshot = nil; storageText = "Not checked."; shareIntent = nil
                    confirming = false; busy = false; status = "Refresh after returning to the foreground."
                }
            }
            .onDisappear {
                epoch = UUID(); snapshot = nil; storageText = "Not checked."; shareIntent = nil
                confirming = false; busy = false; status = "Capture a new snapshot after returning."
            }
        }
    }

    @MainActor private func capture() async {
        guard !busy, phase == .active else { return }
        busy = true
        let attempt = epoch
        let result = await CompanionHealthProbe.capture()
        guard attempt == epoch else { return }
        snapshot = result; storageText = CompanionHealthProbe.localStorageText()
        status = "Snapshot captured locally. Nothing sent."; busy = false
    }

    @MainActor private func sendReviewed() async {
        guard let intent = shareIntent, !busy, phase == .active else { return }
        shareIntent = nil; busy = true
        let attempt = epoch
        do {
            try await session.sendHealthSnapshot(intent.snapshot, expected: intent.connection)
            if attempt == epoch { status = "Windows acknowledged this snapshot. No repair was run." }
        } catch {
            if attempt == epoch { status = "No accepted receipt was confirmed. Check connection/version, capture again and choose Send yourself; no automatic retry." }
        }
        if attempt == epoch { busy = false }
    }
}

private struct HealthShareIntent {
    let snapshot: CompanionHealthSnapshot
    let connection: ConnectedWindowsSession
}
