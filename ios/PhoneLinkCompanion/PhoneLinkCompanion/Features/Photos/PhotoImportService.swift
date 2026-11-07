import Foundation
import CoreTransferable
import PhotosUI
import SwiftUI
import UniformTypeIdentifiers
import UIKit

@MainActor
final class PhotoImportService: ObservableObject {
    enum State: Equatable {
        case idle
        case loading(current: Int, total: Int)
        case sending(current: Int, total: Int, progress: Double)
        case complete(Int)
        case failed(String)
    }

    @Published private(set) var state: State = .idle

    func importSelected(_ items: [PhotosPickerItem], using session: CompanionSession) async {
        guard !items.isEmpty, let destination = session.connectedSession else { return }
        do {
            for (index, item) in items.enumerated() {
                state = .loading(current: index + 1, total: items.count)
                let prepared: (data: Data, displayName: String, mediaType: String)
                if item.supportedContentTypes.contains(where: { $0.conforms(to: .movie) }) {
                    guard destination.capabilities.contains("videos.import.v1") else {
                        throw PhotoImportError.windowsUpdateRequired
                    }
                    guard let video = try await item.loadTransferable(type: SelectedVideo.self) else {
                        throw PhotoImportError.unsupportedFormat
                    }
                    prepared = (video.data, "iPhone Video \(index + 1).\(video.fileExtension)", video.mediaType)
                } else {
                    guard let original = try await item.loadTransferable(type: Data.self) else {
                        throw PhotoImportError.unreadable
                    }
                    prepared = try prepare(original, type: item.supportedContentTypes.first, index: index + 1)
                }
                state = .sending(current: index + 1, total: items.count, progress: 0)
                try await session.sendSelectedMedia(
                    data: prepared.data,
                    displayName: prepared.displayName,
                    mediaType: prepared.mediaType,
                    expected: destination
                ) { [weak self] progress in
                    self?.state = .sending(
                        current: index + 1,
                        total: items.count,
                        progress: progress
                    )
                }
            }
            state = .complete(items.count)
        } catch {
            state = .failed(error.localizedDescription)
        }
    }

    private func prepare(
        _ data: Data,
        type: UTType?,
        index: Int
    ) throws -> (data: Data, displayName: String, mediaType: String) {
        let mediaType = type?.preferredMIMEType ?? ""
        let allowed = ["image/jpeg", "image/png", "image/heic", "image/heif", "image/webp"]
        if allowed.contains(mediaType) {
            let fileExtension = type?.preferredFilenameExtension ?? "image"
            return (data, "iPhone Photo \(index).\(fileExtension)", mediaType)
        }

        guard let image = UIImage(data: data), let jpeg = image.jpegData(compressionQuality: 0.92) else {
            throw PhotoImportError.unsupportedFormat
        }
        return (jpeg, "iPhone Photo \(index).jpg", "image/jpeg")
    }
}

struct SelectedVideo: Transferable {
    static let maximumBytes = 25 * 1024 * 1024
    let data: Data
    let mediaType: String
    let fileExtension: String

    static var transferRepresentation: some TransferRepresentation {
        FileRepresentation(importedContentType: .mpeg4Movie, shouldAttemptToOpenInPlace: true) { received in
            try read(received.file, mediaType: "video/mp4", fileExtension: "mp4")
        }
        FileRepresentation(importedContentType: .quickTimeMovie, shouldAttemptToOpenInPlace: true) { received in
            try read(received.file, mediaType: "video/quicktime", fileExtension: "mov")
        }
    }

    static func read(_ url: URL, mediaType: String, fileExtension: String) throws -> Self {
        let file = try FileHandle(forReadingFrom: url)
        defer { try? file.close() }
        let size = try file.seekToEnd()
        guard size > 0 else { throw PhotoImportError.unreadable }
        guard size <= UInt64(maximumBytes) else { throw PhotoTransferError.tooLarge }
        try file.seek(toOffset: 0)
        var data = Data()
        data.reserveCapacity(Int(size))
        while data.count < Int(size) {
            try Task.checkCancellation()
            guard let chunk = try file.read(upToCount: min(256 * 1024, Int(size) - data.count)), !chunk.isEmpty else {
                throw PhotoImportError.unreadable
            }
            data.append(chunk)
        }
        guard (try file.read(upToCount: 1))?.isEmpty != false else { throw PhotoImportError.unreadable }
        return Self(data: data, mediaType: mediaType, fileExtension: fileExtension)
    }
}

enum PhotoImportError: LocalizedError {
    case unreadable
    case unsupportedFormat
    case windowsUpdateRequired

    var errorDescription: String? {
        switch self {
        case .unreadable: "The selected file could not be loaded."
        case .unsupportedFormat: "The selected format could not be imported. Videos must be MP4 or MOV."
        case .windowsUpdateRequired: "This Windows version does not accept video imports. Update Phone Link on Windows."
        }
    }
}
