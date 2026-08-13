import Foundation
import XCTest
@testable import PhoneLinkCompanion

final class PairingProtocolTests: XCTestCase {
    private let rawOfferJSON = Data(base64Encoded: "eyJ2ZXJzaW9uIjoxLCJzZXNzaW9uSWQiOiI0YWJiZTMxMi1lODFlLTQ1ZWMtOGM4ZS02YTQ3NDE2MWFkN2YiLCJleHBpcmVzQXQiOiIyMDI2LTA4LTEyVDE5OjA2OjMxLjQyN1oiLCJlbmRwb2ludHMiOlt7Imhvc3QiOiIxOTIuMTY4LjEuMjQiLCJwb3J0Ijo0NTgzMX1dLCJ0bHNTcGtpU2hhMjU2IjoiRVJFUkVSRVJFUkVSRVJFUkVSRVJFUkVSRVJFUkVSRVJFUkVSRVJFUkVSRSIsInNlcnZlcklkZW50aXR5S2V5IjoiSWlJaUlpSWlJaUlpSWlJaUlpSWlJaUlpSWlJaUlpSWlJaUlpSWlJaUlpSSIsIm9mZmVyTm9uY2UiOiJNek16TXpNek16TXpNek16TXpNek13IiwicGFpcmluZ1NlY3JldCI6IlJFUkVSRVJFUkVSRVJFUkVSRVJFUkVSRVJFUkVSRVJFUkVSRVJFUkVSRVEifQ==")!

    func testTranscriptMatchesWindowsVector() throws {
        let clientPublicKey = Data(repeating: 0x55, count: 32)
        let clientNonce = Data(repeating: 0x66, count: 32)
        let serverNonce = Data(repeating: 0x77, count: 32)
        let clientDeviceID = "ca8cf3a6-e6ab-4ac7-be7f-6b5736a14ec3"

        let clientDigest = PairingTranscript.clientDigest(
            rawOfferJSON: rawOfferJSON,
            clientDeviceID: clientDeviceID,
            clientIdentityPublicKey: clientPublicKey,
            clientNonce: clientNonce
        )
        XCTAssertEqual(clientDigest.hex, "796cae15a04eb711c6437fcc6fd2eb0fa21cbf8af08d59717815b8e2ec880a84")

        let proof = PairingTranscript.clientProof(
            pairingSecret: Data(repeating: 0x44, count: 32),
            clientDigest: clientDigest
        )
        XCTAssertEqual(proof.hex, "18bbde968f778ee8c353b9ae5fd20d4da3942781fee21407967d9f4369a192b9")

        let serverInput = PairingTranscript.serverSignatureInput(
            rawOfferJSON: rawOfferJSON,
            clientDeviceID: clientDeviceID,
            clientIdentityPublicKey: clientPublicKey,
            clientNonce: clientNonce,
            serverNonce: serverNonce
        )
        XCTAssertEqual(serverInput.hex, "83c205aa19830177b1d069c221648b4dc61ffe4d9d7aefafa478fc91b607a4ba")

        let comparisonDigest = PairingTranscript.comparisonDigest(
            serverSignatureInput: serverInput,
            serverSignature: Data(repeating: 0x88, count: 64)
        )
        XCTAssertEqual(
            try ComparisonPhrase.create(from: comparisonDigest),
            "harbor-moon dawn-elm bold-meadow misty-field harbor-falcon aqua-elm"
        )
    }

    func testDuplicateJSONFieldIsRejected() {
        let duplicate = Data(#"{"version":1,"version":1}"#.utf8)
        XCTAssertThrowsError(try StrictJSON.validateNoDuplicateObjectKeys(duplicate))
    }
}

private extension Data {
    var hex: String { map { String(format: "%02x", $0) }.joined() }
}
