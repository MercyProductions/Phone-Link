import Foundation

enum Base64URL {
    static func encode(_ data: Data) -> String {
        data.base64EncodedString()
            .replacingOccurrences(of: "=", with: "")
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
    }

    static func decode(_ value: String) throws -> Data {
        guard !value.isEmpty,
              value.utf8.allSatisfy({ byte in
                  (byte >= 48 && byte <= 57) ||
                      (byte >= 65 && byte <= 90) ||
                      (byte >= 97 && byte <= 122) ||
                      byte == 45 || byte == 95
              }) else {
            throw PairingOfferError.invalidBase64URL
        }

        var padded = value
            .replacingOccurrences(of: "-", with: "+")
            .replacingOccurrences(of: "_", with: "/")

        switch padded.count % 4 {
        case 0:
            break
        case 2:
            padded += "=="
        case 3:
            padded += "="
        default:
            throw PairingOfferError.invalidBase64URL
        }

        guard let decoded = Data(base64Encoded: padded) else {
            throw PairingOfferError.invalidBase64URL
        }
        return decoded
    }
}
