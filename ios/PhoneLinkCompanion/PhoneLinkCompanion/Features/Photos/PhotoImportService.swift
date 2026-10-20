import Foundation
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
        guard !items.isEmpty else { return }
        do {
            for (index, item) in items.enumerated() {
                state = .loading(current: index + 1, total: items.count)
                guard let original = try await item.loadTransferable(type: Data.self) else {
                    throw PhotoImportError.unreadable
                }

                let prepared = try prepare(
                    original,
                    type: item.supportedContentTypes.first,
                    index: index + 1
                )
                state = .sending(current: index + 1, total: items.count, progress: 0)
                try await session.sendPhoto(
                    data: prepared.data,
                    displayName: prepared.displayName,
                    mediaType: prepared.mediaType
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

enum PhotoImportError: LocalizedError {
    case unreadable
    case unsupportedFormat

    var errorDescription: String? {
        switch self {
        case .unreadable: "The selected photo could not be loaded."
        case .unsupportedFormat: "The selected image format could not be converted for import."
        }
    }
}
