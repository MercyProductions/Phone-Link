import SwiftUI
import VisionKit
import UniformTypeIdentifiers

struct PairingView: View {
    var pairingURL: URL? = nil
    @EnvironmentObject private var companionSession: CompanionSession
    @State private var isShowingScanner = false
    @State private var isShowingFileImporter = false
    @State private var fileImportID: UUID?
    @State private var scannedOffer: PairingOfferArtifact?
    @State private var pendingConnection: PendingPairingConnection?
    @State private var connectTask: Task<Void, Never>?
    @State private var isWorking = false
    @State private var errorMessage: String?
    @State private var didPair = false

    private let client = PairingClient()

    var body: some View {
        ScrollView {
            VStack(spacing: 24) {
                Image(systemName: didPair ? "checkmark.circle.fill" : "qrcode.viewfinder")
                    .font(.system(size: 64, weight: .medium))
                    .foregroundStyle(didPair ? .green : .indigo)

                if didPair {
                    Text("iPhone paired")
                        .font(.title2.bold())
                    Text("Your PC is now a trusted device. Phone Link will reconnect over your local network.")
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                } else if let pendingConnection {
                    comparisonView(pendingConnection)
                } else if let scannedOffer {
                    offerView(scannedOffer)
                } else {
                    scanInstructions
                }

                if let errorMessage {
                    Label(errorMessage, systemImage: "exclamationmark.triangle.fill")
                        .foregroundStyle(.red)
                        .font(.footnote)
                        .padding()
                        .background(.red.opacity(0.08), in: RoundedRectangle(cornerRadius: 14))
                }
            }
            .frame(maxWidth: 520)
            .padding(28)
            .frame(maxWidth: .infinity)
        }
        .navigationTitle("Pair a PC")
        .navigationBarTitleDisplayMode(.inline)
        .task(id: pairingURL) {
            if let pairingURL { handleScan(.success(pairingURL.absoluteString)) }
        }
        .fullScreenCover(isPresented: $isShowingScanner) {
            NavigationStack {
                QRScannerView { result in
                    handleScan(result)
                }
                .ignoresSafeArea(edges: .bottom)
                .navigationTitle("Scan Windows QR code")
                .navigationBarTitleDisplayMode(.inline)
                .toolbar {
                    ToolbarItem(placement: .cancellationAction) {
                        Button("Cancel") { isShowingScanner = false }
                    }
                }
            }
        }
        .onDisappear {
            fileImportID = nil
            connectTask?.cancel()
            pendingConnection?.cancel()
        }
        .fileImporter(isPresented: $isShowingFileImporter,
            allowedContentTypes: [UTType(exportedAs: "com.mercyproductions.phonelink.pairing-offer", conformingTo: .plainText)]) { result in
            let id = UUID()
            fileImportID = id
            Task { @MainActor in
                defer { if fileImportID == id { fileImportID = nil } }
                do {
                    let url = try result.get()
                    let payload = try await Task.detached(priority: .userInitiated) { try PairingOfferCodec.readFile(at: url) }.value
                    guard fileImportID == id, scannedOffer == nil, pendingConnection == nil else { return }
                    handleScan(.success(payload))
                } catch {
                    if fileImportID == id { errorMessage = "Could not open that pairing file. Use a fresh .plpair file saved by your own Windows PC." }
                }
            }
        }
    }

    private var scanInstructions: some View {
        VStack(spacing: 18) {
            VStack(spacing: 8) {
                Text("Open Phone Link on Windows")
                    .font(.title2.bold())
                Text("On Connected Devices, choose Pair iPhone. Keep both devices on the same trusted Wi-Fi network.")
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
            }

            Button {
                errorMessage = nil
                guard DataScannerViewController.isSupported,
                      DataScannerViewController.isAvailable else {
                    errorMessage = "QR scanning is not available on this device."
                    return
                }
                isShowingScanner = true
            } label: {
                Label("Scan QR code", systemImage: "camera.viewfinder")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .disabled(fileImportID != nil)

            Button("Open pairing file") { errorMessage = nil; isShowingFileImporter = true }
                .buttonStyle(.bordered)
                .disabled(fileImportID != nil)
            Text("Use a file transferred from your own Windows PC. Both devices still need the same local network.")
                .font(.footnote).foregroundStyle(.secondary)

            Label("The QR code expires after two minutes and can be used only once.", systemImage: "lock.shield")
                .font(.footnote)
                .foregroundStyle(.secondary)
        }
    }

    private func offerView(_ artifact: PairingOfferArtifact) -> some View {
        VStack(spacing: 18) {
            Text("Windows PC found")
                .font(.title2.bold())
            Text("Identity \(artifact.serverFingerprint)")
                .font(.caption.monospaced())
                .foregroundStyle(.secondary)
            Text("Connect only if you started pairing on the PC in front of you.")
                .multilineTextAlignment(.center)
                .foregroundStyle(.secondary)

            Button("Connect securely") {
                guard connectTask == nil else { return }
                connectTask = Task { await connect(artifact) }
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .disabled(isWorking)

            if isWorking { ProgressView("Authenticating Windows…") }
            Button("Choose a different offer") {
                scannedOffer = nil
                errorMessage = nil
            }
            .buttonStyle(.borderless)
            .disabled(isWorking)
        }
    }

    private func comparisonView(_ connection: PendingPairingConnection) -> some View {
        VStack(spacing: 18) {
            Text("Compare these words")
                .font(.title2.bold())
            Text(connection.comparisonPhrase)
                .font(.title3.monospaced().weight(.semibold))
                .multilineTextAlignment(.center)
                .textSelection(.enabled)
                .padding()
                .background(.thinMaterial, in: RoundedRectangle(cornerRadius: 16))
            Text("Confirm only if Windows shows exactly the same six code words.")
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)

            HStack {
                Button("They don’t match", role: .destructive) {
                    Task { await finish(connection, approved: false) }
                }
                .buttonStyle(.bordered)

                Button("They match") {
                    Task { await finish(connection, approved: true) }
                }
                .buttonStyle(.borderedProminent)
            }
            .disabled(isWorking)
            if isWorking { ProgressView("Waiting for Windows…") }
        }
    }

    private func handleScan(_ result: Result<String, Error>) {
        isShowingScanner = false
        do {
            scannedOffer = try PairingOfferCodec.decode(try result.get())
            errorMessage = nil
        } catch {
            scannedOffer = nil
            errorMessage = error.localizedDescription
        }
    }

    private func connect(_ artifact: PairingOfferArtifact) async {
        isWorking = true
        defer { isWorking = false; connectTask = nil }
        do {
            let connection = try await client.connect(to: artifact)
            if Task.isCancelled { connection.cancel(); return }
            pendingConnection = connection
            errorMessage = nil
        } catch {
            if Task.isCancelled { return }
            errorMessage = error.localizedDescription
        }
    }

    private func finish(_ connection: PendingPairingConnection, approved: Bool) async {
        isWorking = true
        defer { isWorking = false }
        do {
            didPair = try await connection.confirm(approved)
            if didPair {
                companionSession.refreshTrustedPeers()
            }
            pendingConnection = nil
            scannedOffer = nil
            if !didPair { errorMessage = "Pairing was cancelled on one of the devices." }
        } catch {
            pendingConnection = nil
            errorMessage = error.localizedDescription
        }
    }
}
