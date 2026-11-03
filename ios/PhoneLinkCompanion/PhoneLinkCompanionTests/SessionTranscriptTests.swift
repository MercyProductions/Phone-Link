import Foundation
import Testing
@testable import PhoneLinkCompanion

struct SessionTranscriptTests {
    @Test func ownLoadedRuntimeImageHasConsistentMarkerAndNoPaths() throws {
        let result = try RuntimeImageProbe.capture()
        let base = try #require(RuntimeImageSnapshot.hexValue(result.preferredTextAddress))
        let offset = try #require(RuntimeImageSnapshot.hexValue(result.markerImageOffset))
        let address = try #require(RuntimeImageSnapshot.hexValue(result.markerRuntimeAddress))
        #expect(result.resolveTextAddress(base + offset, expectedUUID: result.imageUUID,
            expectedCpuType: result.cpuType, expectedCpuSubtype: result.cpuSubtype) == address)
        #expect(result.resolveTextAddress(base, expectedUUID: UUID().uuidString,
            expectedCpuType: result.cpuType, expectedCpuSubtype: result.cpuSubtype) == nil)
        #expect(result.resolveTextAddress(base, expectedUUID: result.imageUUID,
            expectedCpuType: result.cpuType ^ 1, expectedCpuSubtype: result.cpuSubtype) == nil)
        #expect(result.resolveTextAddress(base, expectedUUID: result.imageUUID,
            expectedCpuType: result.cpuType, expectedCpuSubtype: result.cpuSubtype ^ 1) == nil)
        let json = try #require(JSONSerialization.jsonObject(with: JSONEncoder().encode(result)) as? [String: Any])
        #expect(Set(json.keys) == ["schemaVersion", "capturedAt", "scope", "imageUUID", "cpuType", "cpuSubtype",
            "preferredTextAddress", "loadedHeaderAddress", "textVirtualSize", "slide", "markerRuntimeAddress", "markerImageOffset"])
    }

    @Test func runtimeParserHandlesNegativeSlideAndRejectsMalformedMetadata() throws {
        let fixture = runtimeHeaderFixture()
        let result = try RuntimeImageProbe.parse(fixture, headerAddress: 0x800, markerAddress: 0x900)
        #expect(result.slide == "-0x800")
        #expect(result.markerImageOffset == "0x100")
        #expect(result.resolveTextAddress(0x1100, expectedUUID: result.imageUUID,
            expectedCpuType: result.cpuType, expectedCpuSubtype: result.cpuSubtype) == 0x900)
        #expect(result.resolveTextAddress(0x2000, expectedUUID: result.imageUUID,
            expectedCpuType: result.cpuType, expectedCpuSubtype: result.cpuSubtype) == nil)
        #expect(result.resolveTextAddress(0xFFF, expectedUUID: result.imageUUID,
            expectedCpuType: result.cpuType, expectedCpuSubtype: result.cpuSubtype) == nil)
        #expect(throws: RuntimeImageError.self) { try RuntimeImageProbe.parse(Data(fixture.dropLast()), headerAddress: 0x800, markerAddress: 0x900) }
        #expect(throws: RuntimeImageError.self) { try RuntimeImageProbe.parse(fixture, headerAddress: 0x800, markerAddress: 0x1800) }
        #expect(throws: RuntimeImageError.self) { try RuntimeImageProbe.parse(fixture, headerAddress: UInt64.max - 1, markerAddress: UInt64.max) }
        var invalid = fixture; invalid[36] = 0
        #expect(throws: RuntimeImageError.self) { try RuntimeImageProbe.parse(invalid, headerAddress: 0x800, markerAddress: 0x900) }
        var noUUID = fixture; noUUID[104] = 0
        #expect(throws: RuntimeImageError.self) { try RuntimeImageProbe.parse(noUUID, headerAddress: 0x800, markerAddress: 0x900) }
    }

    private func runtimeHeaderFixture() -> Data {
        var data = Data(repeating: 0, count: 128)
        func u32(_ offset: Int, _ value: UInt32) {
            for byte in 0..<4 { data[offset + byte] = UInt8(truncatingIfNeeded: value >> (8 * byte)) }
        }
        u32(0, 0xfeedfacf); u32(4, 0x100000c); u32(8, 0); u32(12, 6); u32(16, 2); u32(20, 96)
        u32(32, 0x19); u32(36, 72)
        data.replaceSubrange(40..<46, with: "__TEXT".utf8)
        u32(56, 0x1000); u32(64, 0x1000); u32(80, 0x1000)
        u32(104, 0x1b); u32(108, 24)
        for byte in 0..<16 { data[112 + byte] = UInt8(byte) }
        return data
    }

    @Test func touchCoverageCountsOnlyReceivedCellsAndBoundsEdges() {
        var coverage = TouchCoverage()
        let size = CGSize(width: 60, height: 80)
        coverage.record(CGPoint(x: 0, y: 0), in: size)
        coverage.record(CGPoint(x: 1, y: 1), in: size)
        #expect(coverage.cells == [0])
        coverage.record(CGPoint(x: 60, y: 80), in: size)
        #expect(coverage.cells == [0, 47]) // No interpolation across unobserved cells.
        for row in 0..<8 {
            for column in 0..<6 {
                coverage.record(CGPoint(x: column * 10 + 5, y: row * 10 + 5), in: size)
            }
        }
        #expect(coverage.cells.count == 48)
        coverage = TouchCoverage()
        #expect(coverage.cells.isEmpty)
    }

    @Test func invalidTouchGeometryDoesNotInventCoverage() {
        var coverage = TouchCoverage()
        let size = CGSize(width: 60, height: 80)
        for point in [CGPoint(x: -1, y: 0), CGPoint(x: 61, y: 1), CGPoint(x: 1, y: 81),
                      CGPoint(x: CGFloat.nan, y: 0), CGPoint(x: 0, y: CGFloat.infinity)] {
            coverage.record(point, in: size)
        }
        for invalid in [CGSize.zero, CGSize(width: -1, height: 1), CGSize(width: CGFloat.infinity, height: 1)] {
            coverage.record(.zero, in: invalid)
        }
        #expect(coverage.cells.isEmpty)
    }

    @Test func generatedToneIsBoundedFadedPCMWithoutRecording() throws {
        let data = CheckTone.wav()
        #expect(String(decoding: data.prefix(4), as: UTF8.self) == "RIFF")
        #expect(String(decoding: data[8..<16], as: UTF8.self) == "WAVEfmt ")
        func u16(_ offset: Int) -> UInt16 { UInt16(data[offset]) | UInt16(data[offset + 1]) << 8 }
        func u32(_ offset: Int) -> UInt32 { UInt32(u16(offset)) | UInt32(u16(offset + 2)) << 16 }
        #expect(u32(4) == UInt32(data.count - 8))
        #expect(u16(20) == 1 && u16(22) == 1 && u16(34) == 16)
        #expect(u32(24) == 22_050 && u32(40) == 17_640)
        #expect(data.count == 17_684)
        let samples = stride(from: 44, to: data.count, by: 2).map { Int(Int16(bitPattern: u16($0))) }
        #expect(samples.first == 0 && samples.last == 0)
        #expect(try #require(samples.map { abs($0) }.max()) <= 4_916)
        #expect(samples.contains { $0 > 0 } && samples.contains { $0 < 0 })
    }

    @Test func healthChargeValidationDoesNotTurnUnknownIntoZero() {
        for value in [Float(-1), Float(1.1), Float.nan, Float.infinity] {
            #expect(CompanionHealthSnapshot.percentage(value) == nil)
        }
        #expect(CompanionHealthSnapshot.percentage(0) == 0)
        #expect(CompanionHealthSnapshot.percentage(1) == 100)
        #expect(CompanionHealthSnapshot.percentage(0.42) == 42)
    }

    @Test func healthWireFieldsExcludeStorageAndIdentifiersAndEncodeNull() throws {
        let value = CompanionHealthSnapshot(schemaVersion: 1, capturedAt: "2026-11-03T10:00:00.000Z",
            batteryPercent: nil, batteryState: "unknown", thermalState: "nominal", lowPowerMode: false,
            contactsPermission: "limited", photosPermission: "limited", cameraPermission: "not-determined",
            notificationPermission: "provisional")
        let data = try JSONEncoder().encode(value)
        let fields = try #require(JSONSerialization.jsonObject(with: data) as? [String: Any])
        #expect(Set(fields.keys) == Set(["schemaVersion", "capturedAt", "batteryPercent", "batteryState",
            "thermalState", "lowPowerMode", "contactsPermission", "photosPermission", "cameraPermission", "notificationPermission"]))
        #expect(fields["batteryPercent"] is NSNull)
        #expect(try JSONDecoder().decode(CompanionHealthSnapshot.self, from: data) == value)
    }

    @MainActor @Test func healthProbeReadsWithoutRequestingPermissions() async {
        let value = await CompanionHealthProbe.capture()
        #expect(value.schemaVersion == 1)
        #expect(value.batteryPercent == nil || (0...100).contains(value.batteryPercent!))
        #expect(["unknown", "nominal", "fair", "serious", "critical"].contains(value.thermalState))
    }
    @Test func contactOutboxesAreIsolatedByPeer() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = ContactSyncStateStore(directory: directory)
        let first = UUID()
        let second = UUID()
        try store.save(ContactSyncState(peerDeviceID: first, lastSequence: 90, pending: nil))
        #expect(try store.load(peerID: first).lastSequence == 90)
        #expect(try store.load(peerID: second).lastSequence == 0)
        try store.save(ContactSyncState(peerDeviceID: second, lastSequence: 3, pending: nil))
        try store.remove(peerID: first)
        #expect(try store.load(peerID: second).lastSequence == 3)
    }

    @Test func pendingRecoveryDoesNotReplayRevokedContactFields() throws {
        let contact = ContactPayload(sourceContactId: "revoked", displayName: "Private Contact",
            givenName: "Private", familyName: "Contact", organizationName: "", phoneNumbers: [], emailAddresses: [])
        let applied = ContactSyncEvent(eventId: UUID(), originSequence: 1, occurredAt: PhoneLinkJSON.timestamp(),
            eventType: "contact.upsert", entityId: "revoked", payload: contact)
        let unsent = ContactSyncEvent(eventId: UUID(), originSequence: 2, occurredAt: PhoneLinkJSON.timestamp(),
            eventType: "contact.upsert", entityId: "revoked", payload: contact)
        let pending = PendingContactSync(batches: [ContactSyncBatchPayload(batchId: UUID(), events: [applied, unsent])],
            resultingIdentifiers: ["revoked"])
        let recovered = try #require(pending.recovered(after: 1, contacts: []))
        let event = try #require(recovered.batches.first?.events.first)
        #expect(event.originSequence == 2)
        #expect(event.eventId != unsent.eventId)
        #expect(event.eventType == "contact.delete")
        #expect(event.payload.displayName.isEmpty)
        #expect(!String(decoding: try PhoneLinkJSON.encoder.encode(recovered), as: UTF8.self).contains("Private Contact"))
    }

    @Test @MainActor func disconnectInvalidatesAnInFlightDiscoveryAttempt() async throws {
        let discovery = SuspendedDiscovery()
        let client = DeviceSessionClient(discoverEndpoints: { await discovery.endpoints() })
        let peer = TrustedWindowsPeer(
            deviceID: UUID(), identityPublicKey: Data(repeating: 1, count: 32),
            tlsSPKISHA256: Data(repeating: 2, count: 32), endpoints: [], pairedAt: Date()
        )
        let attempt = Task { try await client.connect(to: peer) }
        await discovery.waitUntilStarted()
        client.disconnect()
        discovery.finish()
        do {
            _ = try await attempt.value
            Issue.record("A disconnected connection attempt must not complete.")
        } catch is CancellationError {
            #expect(client.connection == nil)
        }
    }

    @Test func matchesSharedDotNetVector() throws {
        let serverProofInput = SessionTranscript.serverProofInput(
            connectionID: try #require(UUID(uuidString: "11111111-2222-3333-4444-555555555555")),
            sessionID: try #require(UUID(uuidString: "66666666-7777-8888-9999-aaaaaaaaaaaa")),
            serverDeviceID: try #require(UUID(uuidString: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")),
            clientDeviceID: try #require(UUID(uuidString: "ffffffff-eeee-dddd-cccc-bbbbbbbbbbbb")),
            clientNonce: Data((0..<32).map(UInt8.init)),
            serverNonce: Data((32..<64).map(UInt8.init)),
            selectedProtocolVersion: 1,
            clientAppVersion: "1.2.3"
        )
        #expect(serverProofInput.hex == "c65d3942de65d3611c8d4c10615ed03a4dd233209af33abf823ce5d16139c3ae")

        let clientProofInput = SessionTranscript.clientProofInput(
            serverProofInput: serverProofInput,
            serverSignature: Data((64..<128).map(UInt8.init))
        )
        #expect(clientProofInput.hex == "8a8d05c4cfb4a3e9e73dd0aa0ca486dfd8eb07b8eaa17a345e72bfd763d60474")
    }
}

@MainActor
private final class SuspendedDiscovery {
    private var started = false
    private var startWaiter: CheckedContinuation<Void, Never>?
    private var resultWaiter: CheckedContinuation<[PairingEndpoint], Never>?

    func endpoints() async -> [PairingEndpoint] {
        started = true
        startWaiter?.resume()
        startWaiter = nil
        return await withCheckedContinuation { resultWaiter = $0 }
    }

    func waitUntilStarted() async {
        if started { return }
        await withCheckedContinuation { startWaiter = $0 }
    }

    func finish() {
        resultWaiter?.resume(returning: [])
        resultWaiter = nil
    }
}

private extension Data {
    var hex: String { map { String(format: "%02x", $0) }.joined() }
}
