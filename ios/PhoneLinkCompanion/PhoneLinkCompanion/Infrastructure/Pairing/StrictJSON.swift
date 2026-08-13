import Foundation

enum StrictJSON {
    static func validateNoDuplicateObjectKeys(_ data: Data) throws {
        enum Container {
            case object(keys: Set<String>, expectsKey: Bool)
            case array
        }

        let bytes = Array(data)
        var stack: [Container] = []
        var index = 0

        while index < bytes.count {
            let byte = bytes[index]
            switch byte {
            case 0x7B: // {
                stack.append(.object(keys: [], expectsKey: true))
                index += 1
            case 0x5B: // [
                stack.append(.array)
                index += 1
            case 0x7D, 0x5D: // } or ]
                guard !stack.isEmpty else { throw PairingOfferError.invalidJSON }
                stack.removeLast()
                index += 1
            case 0x2C: // ,
                if case .object(let keys, _) = stack.last {
                    stack[stack.count - 1] = .object(keys: keys, expectsKey: true)
                }
                index += 1
            case 0x22: // "
                let start = index
                index += 1
                var escaped = false
                while index < bytes.count {
                    let current = bytes[index]
                    if escaped {
                        escaped = false
                    } else if current == 0x5C {
                        escaped = true
                    } else if current == 0x22 {
                        break
                    }
                    index += 1
                }

                guard index < bytes.count else { throw PairingOfferError.invalidJSON }

                if case .object(var keys, true) = stack.last {
                    let literal = Data(bytes[start...index])
                    let key = try JSONDecoder().decode(String.self, from: literal)
                    guard keys.insert(key).inserted else {
                        throw PairingOfferError.duplicateJSONProperty(key)
                    }
                    stack[stack.count - 1] = .object(keys: keys, expectsKey: false)
                }
                index += 1
            default:
                index += 1
            }
        }

        guard stack.isEmpty else { throw PairingOfferError.invalidJSON }
    }
}
