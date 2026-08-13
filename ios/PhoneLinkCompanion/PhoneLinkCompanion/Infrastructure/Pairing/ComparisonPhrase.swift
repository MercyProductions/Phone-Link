import Foundation

enum ComparisonPhrase {
    private static let prefixes = "amber aqua arctic autumn bold bright calm cedar coral cosmic crisp dawn deep ember fern frost gentle golden harbor hazel indigo ivory jade lunar maple misty navy olive quiet rapid silver vivid".split(separator: " ").map(String.init)
    private static let nouns = "acorn anchor apple arrow atlas badger beacon birch bison blossom bridge brook cabin canyon castle cloud comet crane creek crown delta dune eagle elm falcon field finch flame forest fox garden glacier grove hawk hill island lake lantern leaf lion meadow mesa moon oak ocean orbit owl pebble pine planet quartz raven reef river robin sail shore sky sparrow star stone summit sun tiger".split(separator: " ").map(String.init)

    static func create(from comparisonDigest: Data) throws -> String {
        guard comparisonDigest.count >= 9, prefixes.count == 32, nouns.count == 64 else {
            throw PairingOfferError.invalidFieldLength
        }

        let bytes = [UInt8](comparisonDigest)
        let words = (0..<6).map { wordIndex -> String in
            let tableIndex = readElevenBits(bytes, startingAt: wordIndex * 11)
            return "\(prefixes[tableIndex >> 6])-\(nouns[tableIndex & 0x3F])"
        }
        return words.joined(separator: " ")
    }

    private static func readElevenBits(_ bytes: [UInt8], startingAt startBit: Int) -> Int {
        var result = 0
        for offset in 0..<11 {
            let bit = startBit + offset
            let value = (bytes[bit / 8] >> (7 - (bit % 8))) & 1
            result = (result << 1) | Int(value)
        }
        return result
    }
}
