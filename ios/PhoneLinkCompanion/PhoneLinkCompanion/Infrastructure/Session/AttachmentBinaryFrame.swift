import CryptoKit
import Foundation

enum AttachmentBinaryFrame {
    static let maximumChunkBytes = 256 * 1024

    static func encode(attachmentID: UUID, chunkIndex: UInt32, payload: Data) throws -> Data {
        guard !payload.isEmpty, payload.count <= maximumChunkBytes else {
            throw PhotoTransferError.invalidChunk
        }

        var frame = Data("PLB1".utf8)
        frame.append(contentsOf: [1, 1, 0, 0])
        var uuid = attachmentID.uuid
        withUnsafeBytes(of: &uuid) { frame.append(contentsOf: $0) }
        appendBigEndian(chunkIndex, to: &frame)
        appendBigEndian(UInt32(payload.count), to: &frame)
        frame.append(contentsOf: SHA256.hash(data: payload))
        frame.append(payload)
        return frame
    }

    private static func appendBigEndian(_ value: UInt32, to data: inout Data) {
        var bigEndian = value.bigEndian
        withUnsafeBytes(of: &bigEndian) { data.append(contentsOf: $0) }
    }
}
