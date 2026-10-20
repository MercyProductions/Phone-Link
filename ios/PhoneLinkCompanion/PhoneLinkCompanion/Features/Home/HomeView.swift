import SwiftUI

struct HomeView: View {
    @EnvironmentObject private var session: CompanionSession

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: 24) {
                    Image(systemName: "iphone.and.arrow.forward")
                        .font(.system(size: 58, weight: .semibold))
                        .symbolRenderingMode(.hierarchical)
                        .foregroundStyle(.indigo)
                        .accessibilityHidden(true)

                    VStack(spacing: 8) {
                        Text("Connect this iPhone")
                            .font(.largeTitle.bold())
                            .multilineTextAlignment(.center)

                        Text("Pair directly with your Windows PC. Your private data stays on your devices and local network.")
                            .foregroundStyle(.secondary)
                            .multilineTextAlignment(.center)
                    }

                    IdentityCard(state: session.startupState)

                    ConnectionCard(
                        state: session.connectionState,
                        reconnect: reconnect,
                        disconnect: session.disconnect
                    )
                    if let reconnectStatus = session.reconnectStatus {
                        Text(reconnectStatus).font(.footnote).foregroundStyle(.secondary)
                    }

                    NavigationLink {
                        PairingView()
                    } label: {
                        Label("Pair with Windows", systemImage: "qrcode.viewfinder")
                            .frame(maxWidth: .infinity)
                            .fontWeight(.semibold)
                    }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.large)
                    .disabled(!isIdentityReady)

                    VStack(alignment: .leading, spacing: 14) {
                        PrivacyRow(icon: "lock.shield", title: "End-to-end authenticated", detail: "Each trusted device has a unique cryptographic identity.")
                        PrivacyRow(icon: "wifi", title: "Local by default", detail: "Sync uses your local network; no relay service is required.")
                        PrivacyRow(icon: "person.crop.circle.badge.checkmark", title: "You choose permissions", detail: "Contacts and notifications are requested only when their features are enabled.")
                    }
                }
                .frame(maxWidth: 560)
                .padding(.horizontal, 24)
                .padding(.vertical, 32)
                .frame(maxWidth: .infinity)
            }
            .background(Color(.systemGroupedBackground))
            .navigationTitle("Phone Link")
            .task {
                session.refreshTrustedPeers()
            }
        }
    }

    private var isIdentityReady: Bool {
        if case .ready = session.startupState { return true }
        return false
    }

    private func reconnect() {
        guard let peer = session.preferredPeer else { return }
        Task { await session.connect(to: peer) }
    }
}

private struct ConnectionCard: View {
    let state: CompanionSession.ConnectionState
    let reconnect: () -> Void
    let disconnect: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            switch state {
            case .disconnected:
                Label("No PC connected", systemImage: "wifi.slash")
                Spacer()
                Button("Connect", action: reconnect)
            case .connecting:
                ProgressView()
                Text("Authenticating PC…")
                Spacer()
            case .connected:
                Label("Securely connected", systemImage: "lock.shield.fill")
                    .foregroundStyle(.green)
                Spacer()
                Button("Disconnect", action: disconnect)
            case .failed(let message):
                Image(systemName: "exclamationmark.triangle.fill")
                    .foregroundStyle(.orange)
                Text(message)
                    .font(.footnote)
                    .lineLimit(2)
                Spacer()
                Button("Retry", action: reconnect)
            }
        }
        .padding(16)
        .frame(maxWidth: .infinity)
        .background(.background, in: RoundedRectangle(cornerRadius: 18, style: .continuous))
    }
}

private struct IdentityCard: View {
    let state: CompanionSession.StartupState

    var body: some View {
        Group {
            switch state {
            case .starting:
                HStack(spacing: 12) {
                    ProgressView()
                    Text("Creating a protected device identity…")
                }
            case .ready(let identity):
                VStack(alignment: .leading, spacing: 10) {
                    Label("Ready to pair", systemImage: "checkmark.circle.fill")
                        .font(.headline)
                        .foregroundStyle(.green)
                    Text(identity.deviceName)
                        .font(.title3.weight(.semibold))
                    Text("Identity \(identity.fingerprint)")
                        .font(.caption.monospaced())
                        .foregroundStyle(.secondary)
                        .textSelection(.enabled)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            case .failed(let message):
                Label(message, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red)
            }
        }
        .padding(18)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.background, in: RoundedRectangle(cornerRadius: 18, style: .continuous))
    }
}

private struct PrivacyRow: View {
    let icon: String
    let title: String
    let detail: String

    var body: some View {
        HStack(alignment: .top, spacing: 14) {
            Image(systemName: icon)
                .font(.title3)
                .foregroundStyle(.indigo)
                .frame(width: 30)
            VStack(alignment: .leading, spacing: 3) {
                Text(title).fontWeight(.semibold)
                Text(detail)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }
        }
    }
}
