import Foundation
import Testing
@testable import PhoneLinkCompanion

struct SessionTranscriptTests {
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
        let client = DeviceSessionClient()
        let peer = TrustedWindowsPeer(
            deviceID: UUID(), identityPublicKey: Data(repeating: 1, count: 32),
            tlsSPKISHA256: Data(repeating: 2, count: 32), endpoints: [], pairedAt: Date()
        )
        let attempt = Task { try await client.connect(to: peer) }
        await Task.yield()
        client.disconnect()
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

private extension Data {
    var hex: String { map { String(format: "%02x", $0) }.joined() }
}
