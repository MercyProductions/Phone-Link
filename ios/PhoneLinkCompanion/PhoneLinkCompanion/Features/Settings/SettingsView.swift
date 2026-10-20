import SwiftUI

struct SettingsView: View {
    @EnvironmentObject private var session: CompanionSession
    @State private var peerToRevoke: UUID?
    @State private var showRevokeConfirmation = false

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    Text("Contacts and photos are sent only when you choose to synchronize or share them.")
                    Text("Other apps' notifications require the separate Windows Bluetooth integration.")
                } header: {
                    Text("Privacy")
                } footer: {
                    Text("No developer server receives your phone data.")
                }

                Section("Connection") {
                    LabeledContent("Preferred transport", value: "Local Wi-Fi")
                    Text("Automatic reconnection runs while Phone Link is in the foreground.")
                    Button("Disconnect and pause reconnection") { session.disconnect() }
                }

                Section("Trusted PCs") {
                    ForEach(session.trustedPeers, id: \.deviceID) { peer in
                        VStack(alignment: .leading, spacing: 8) {
                            Text("PC · \(peer.deviceID.uuidString.prefix(8))")
                            Button("Connect") { Task { await session.connect(to: peer) } }
                            Button("Revoke access", role: .destructive) {
                                peerToRevoke = peer.deviceID
                                showRevokeConfirmation = true
                            }
                        }
                    }
                }

                Section("About") {
                    LabeledContent("Protocol", value: "Phone Link v1")
                    LabeledContent("Data path", value: "Direct to your PC")
                }
            }
            .navigationTitle("Settings")
            .confirmationDialog("Revoke this PC?", isPresented: $showRevokeConfirmation) {
                Button("Revoke access", role: .destructive) {
                    if let peerToRevoke { session.revokePeer(peerToRevoke) }
                }
            } message: {
                Text("The connection ends and this iPhone will not reconnect to this PC until you pair again. Windows may retain previously synchronized data.")
            }
        }
    }
}
