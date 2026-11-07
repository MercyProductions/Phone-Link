import SwiftUI
import PhotosUI

struct ContactSyncView: View {
    @EnvironmentObject private var session: CompanionSession
    @StateObject private var contactSync = ContactSyncService()
    @StateObject private var photoImport = PhotoImportService()
    @State private var selectedPhotos: [PhotosPickerItem] = []

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    Label(
                        "Share only contacts allowed by your current iOS Contacts permission.",
                        systemImage: "person.crop.circle.badge.checkmark"
                    )
                    .font(.subheadline)

                    Button {
                        Task { await contactSync.synchronize(using: session) }
                    } label: {
                        Label("Synchronize contacts", systemImage: "arrow.triangle.2.circlepath")
                    }
                    .disabled(!isConnected || isWorking)
                } header: {
                    Text("Contacts")
                } footer: {
                    Text("Phone Link reads names, phone numbers, and email addresses. Contact notes and unrelated fields are never requested.")
                }

                Section("Status") {
                    statusView
                }

                Section {
                    PhotosPicker(
                        selection: $selectedPhotos,
                        maxSelectionCount: 20,
                        matching: acceptsVideo ? .any(of: [.images, .videos]) : .images,
                        preferredItemEncoding: .current
                    ) {
                        Label(acceptsVideo ? "Choose photos or videos to import" : "Choose photos to import", systemImage: "photo.on.rectangle.angled")
                    }
                    .disabled(!isConnected || isPhotoWorking)

                    photoStatusView
                } header: {
                    Text("Photos and videos")
                } footer: {
                    Text(acceptsVideo ? "Only selected items are shared. Photos and MP4 or MOV clips up to 25 MiB each are verified and stored encrypted on Windows." : "Only selected photos are shared. An updated Windows app is required for video imports.")
                }
            }
            .navigationTitle("Sync")
            .onChange(of: selectedPhotos) { _, items in
                guard !items.isEmpty else { return }
                Task {
                    await photoImport.importSelected(items, using: session)
                    selectedPhotos = []
                }
            }
        }
    }

    @ViewBuilder
    private var photoStatusView: some View {
        switch photoImport.state {
        case .idle:
            Text(isConnected ? "Select up to 20 items" : "Connect to a trusted PC first")
                .foregroundStyle(.secondary)
        case .loading(let current, let total):
            ProgressView("Loading item \(current) of \(total)…")
        case .sending(let current, let total, let progress):
            ProgressView(value: progress) {
                Text("Importing item \(current) of \(total)…")
            }
        case .complete(let count):
            Label("Imported \(count) items", systemImage: "checkmark.circle.fill")
                .foregroundStyle(.green)
        case .failed(let message):
            Label(message, systemImage: "exclamationmark.triangle.fill")
                .foregroundStyle(.red)
        }
    }

    @ViewBuilder
    private var statusView: some View {
        switch contactSync.state {
        case .idle:
            Text(isConnected ? "Ready" : "Connect to a trusted PC first")
        case .requestingPermission:
            ProgressView("Checking contact permission…")
        case .reading:
            ProgressView("Reading authorized contacts…")
        case .sending(let completed, let total):
            ProgressView(value: Double(completed), total: Double(total)) {
                Text("Sending encrypted batch \(completed + 1) of \(total)…")
            }
        case .complete(let count):
            Label("Synchronized \(count) contacts", systemImage: "checkmark.circle.fill")
                .foregroundStyle(.green)
        case .failed(let message):
            Label(message, systemImage: "exclamationmark.triangle.fill")
                .foregroundStyle(.red)
        }
    }

    private var isConnected: Bool {
        if case .connected = session.connectionState { return true }
        return false
    }

    private var acceptsVideo: Bool {
        session.connectedSession?.capabilities.contains("videos.import.v1") == true
    }

    private var isWorking: Bool {
        switch contactSync.state {
        case .requestingPermission, .reading, .sending: true
        default: false
        }
    }

    private var isPhotoWorking: Bool {
        switch photoImport.state {
        case .loading, .sending: true
        default: false
        }
    }
}
