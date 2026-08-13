import Foundation
import XCTest
@testable import PhoneLinkCompanion

final class IdentityFingerprintTests: XCTestCase {
    func testFingerprintIsStableAndHumanCheckable() {
        let key = Data([0x01, 0x02, 0x03, 0x04])

        let first = IdentityFingerprint.display(key)
        let second = IdentityFingerprint.display(key)

        XCTAssertEqual(first, second)
        XCTAssertEqual(first.split(separator: ":").count, 6)
        XCTAssertEqual(first.count, 17)
    }

    func testDifferentKeysHaveDifferentFingerprints() {
        XCTAssertNotEqual(
            IdentityFingerprint.display(Data([0x01])),
            IdentityFingerprint.display(Data([0x02]))
        )
    }
}
