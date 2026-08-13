import Foundation
import Testing
@testable import PhoneLinkCompanion

struct SessionTranscriptTests {
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
