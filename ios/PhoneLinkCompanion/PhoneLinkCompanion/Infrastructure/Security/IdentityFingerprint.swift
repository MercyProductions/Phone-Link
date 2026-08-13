import CryptoKit
import Foundation

enum IdentityFingerprint {
    static func display(_ publicKey: Data) -> String {
        SHA256.hash(data: publicKey)
            .prefix(6)
            .map { String(format: "%02X", $0) }
            .joined(separator: ":")
    }
}
