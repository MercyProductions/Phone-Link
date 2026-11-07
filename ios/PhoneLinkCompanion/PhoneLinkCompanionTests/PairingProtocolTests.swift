import Foundation
import XCTest
@testable import PhoneLinkCompanion

final class PairingProtocolTests: XCTestCase {
    func testMessageDraftRequiresOneCanonicalRecipientAndBoundedUtf8() {
        XCTAssertEqual(MessageDraftInput(arguments: ["recipient": "+15550101000", "body": "Hello\n👩‍👩‍👦"])?.recipient, "+15550101000")
        XCTAssertNotNil(MessageDraftInput(arguments: ["recipient": "123", "body": String(repeating: "é", count: 500)]))
        XCTAssertNil(MessageDraftInput(arguments: ["recipient": "123", "body": String(repeating: "é", count: 501)]))
        XCTAssertNil(MessageDraftInput(arguments: ["recipient": "*123", "body": "Hello"]))
        XCTAssertNil(MessageDraftInput(arguments: ["recipient": "123,456", "body": "Hello"]))
        XCTAssertNil(MessageDraftInput(arguments: ["recipient": "123", "body": "bad\u{0}value"]))
        XCTAssertNil(MessageDraftInput(arguments: ["recipient": "123", "body": "Hello", "unexpected": "value"]))
    }

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

    func testApplicationRegistersPairingURLScheme() {
        let types = Bundle.main.object(forInfoDictionaryKey: "CFBundleURLTypes") as? [[String: Any]] ?? []
        let schemes = types.flatMap { $0["CFBundleURLSchemes"] as? [String] ?? [] }
        XCTAssertTrue(schemes.contains("phonelink"))
    }

    func testPairingURLRoundTripPreservesSignedOfferBytes() throws {
        let payload = "phonelink://pair?data=" + Base64URL.encode(rawOfferJSON)
        let url = try XCTUnwrap(URL(string: payload))
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let now = try XCTUnwrap(formatter.date(from: "2026-08-12T19:05:31.427Z"))
        let decoded = try PairingOfferCodec.decode(url.absoluteString, now: now)
        XCTAssertEqual(decoded.rawJSON, rawOfferJSON)
        XCTAssertEqual(decoded.qrPayload, payload)
    }

    func testPairingFilePreservesSignedOfferBytesAndExpiry() throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".plpair")
        defer { try? FileManager.default.removeItem(at: url) }
        let payload = "phonelink://pair?data=" + Base64URL.encode(rawOfferJSON)
        try Data(payload.utf8).write(to: url)
        let text = try PairingOfferCodec.readFile(at: url)
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let now = try XCTUnwrap(formatter.date(from: "2026-08-12T19:05:31.427Z"))
        XCTAssertEqual(try PairingOfferCodec.decode(text, now: now).rawJSON, rawOfferJSON)
        XCTAssertThrowsError(try PairingOfferCodec.decode(text, now: now.addingTimeInterval(180)))
    }

    func testPairingFileRejectsOversizedNonAsciiAndWrongExtension() throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".plpair")
        defer { try? FileManager.default.removeItem(at: url) }
        try Data(repeating: 65, count: PairingOfferCodec.maximumFileBytes + 1).write(to: url)
        XCTAssertThrowsError(try PairingOfferCodec.readFile(at: url))
        try Data([0xff]).write(to: url)
        XCTAssertThrowsError(try PairingOfferCodec.readFile(at: url))
        XCTAssertThrowsError(try PairingOfferCodec.readFile(at: url.appendingPathExtension("txt")))
    }

    @MainActor
    func testPairingFallbackAfterTransportTimeoutUsesRemainingOfferTime() async throws {
        let start = Date(timeIntervalSince1970: 1000)
        let offer = endpointOffer(expiresAt: start.addingTimeInterval(8))
        var now = start
        var attempted: [PairingEndpoint] = []
        var budgets: [Duration] = []
        let chosen = try await PairingEndpointAttempts.firstReachable(offer, now: { now }) { endpoint, budget in
            attempted.append(endpoint)
            budgets.append(budget)
            if attempted.count == 1 {
                now = start.addingTimeInterval(6)
                throw PairingClientError.endpointTimedOut
            }
            return endpoint
        }
        XCTAssertEqual(chosen, offer.endpoints[1])
        XCTAssertEqual(attempted, offer.endpoints)
        XCTAssertEqual(budgets, [.seconds(6), .seconds(2)])
    }

    @MainActor
    func testPairingDoesNotRetryAfterIdentityOrCertificateFailure() async {
        let offer = endpointOffer(expiresAt: Date().addingTimeInterval(30))
        let failures: [any Error] = [PairingClientError.invalidServerSignature, URLError(.serverCertificateUntrusted)]
        for failure in failures {
            var attempts = 0
            do {
                let _: Void = try await PairingEndpointAttempts.firstReachable(offer) { _, _ in
                    attempts += 1
                    throw failure
                }
                XCTFail("An identity error must end the attempt")
            } catch {
                XCTAssertEqual((error as NSError).code, (failure as NSError).code)
                XCTAssertEqual((error as NSError).domain, (failure as NSError).domain)
            }
            XCTAssertEqual(attempts, 1)
        }
    }

    @MainActor
    func testPairingDoesNotAttemptAnotherAddressAfterExpiry() async {
        let start = Date(timeIntervalSince1970: 1000)
        let offer = endpointOffer(expiresAt: start.addingTimeInterval(1))
        var now = start
        var attempts = 0
        do {
            let _: Void = try await PairingEndpointAttempts.firstReachable(offer, now: { now }) { _, _ in
                attempts += 1
                now = offer.expiresAt
                throw URLError(.cannotConnectToHost)
            }
            XCTFail("Expired offers cannot try another address")
        } catch {
            XCTAssertEqual(error as? PairingClientError, .offerExpired)
        }
        XCTAssertEqual(attempts, 1)
    }

    @MainActor
    func testPairingDoesNotStartAnAddressWhenCancelled() async {
        let offer = endpointOffer(expiresAt: Date().addingTimeInterval(30))
        let pending = Task { @MainActor in
            let _: Void = try await PairingEndpointAttempts.firstReachable(offer) { _, _ in
                XCTFail("Cancellation must be checked before starting a connection")
            }
        }
        pending.cancel()
        do {
            try await pending.value
            XCTFail("Expected cancellation")
        } catch {
            XCTAssertTrue(error is CancellationError)
        }
    }

    private func endpointOffer(expiresAt: Date) -> PairingOffer {
        PairingOffer(version: 1, sessionID: UUID(), expiresAt: expiresAt,
            endpoints: [PairingEndpoint(host: "192.168.1.2", port: 1234), PairingEndpoint(host: "10.0.0.2", port: 1234)],
            tlsSPKISHA256: Data(), serverIdentityKey: Data(), offerNonce: Data(), pairingSecret: Data())
    }
}

private extension Data {
    var hex: String { map { String(format: "%02x", $0) }.joined() }
}
