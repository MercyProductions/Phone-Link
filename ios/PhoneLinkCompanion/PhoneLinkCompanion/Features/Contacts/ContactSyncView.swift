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
                Section("Contacts") {
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
                } footer: {
                    Text("Phone Link reads names, phone numbers, and email addresses. Contact notes and unrelated fields are never requested.")
                }

                Section("Status") {
                    statusView
                }

                Section("Photos") {
                    PhotosPicker(
                        selection: $selectedPhotos,
                        maxSelectionCount: 20,
                        matching: .images
                    ) {
                        Label("Choose photos to import", systemImage: "photo.on.rectangle.angled")
                    }
                    .disabled(!isConnected || isPhotoWorking)

                    photoStatusView
                } footer: {
                    Text("The system picker shares only the photos you select. Each photo is verified, encrypted, and stored in Phone Link’s private data folder on Windows.")
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
            Text(isConnected ? "Select up to 20 photos" : "Connect to a trusted PC first")
                .foregroundStyle(.secondary)
        case .loading(let current, let total):
            ProgressView("Loading photo \(current) of \(total)…")
        case .sending(let current, let total, let progress):
            ProgressView(value: progress) {
                Text("Importing photo \(current) of \(total)…")
            }
        case .complete(let count):
            Label("Imported \(count) photos", systemImage: "checkmark.circle.fill")
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
