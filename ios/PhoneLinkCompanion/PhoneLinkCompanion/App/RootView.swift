import SwiftUI

struct RootView: View {
    @EnvironmentObject private var session: CompanionSession
    @State private var pairingLink: PairingLink?

    var body: some View {
        TabView {
            HomeView()
                .tabItem {
                    Label("Phone Link", systemImage: "iphone.and.arrow.forward")
                }

            ContactSyncView()
                .tabItem {
                    Label("Sync", systemImage: "arrow.triangle.2.circlepath")
                }

            SettingsView()
                .tabItem {
                    Label("Settings", systemImage: "gearshape")
                }
        }
        .tint(.indigo)
        .onOpenURL { url in
            // An external link only opens the review screen; it cannot approve trust.
            guard pairingLink == nil else { return }
            pairingLink = PairingLink(url: url)
        }
        .sheet(item: $pairingLink) { link in
            NavigationStack {
                PairingView(pairingURL: link.url)
                    .toolbar {
                        ToolbarItem(placement: .cancellationAction) {
                            Button("Close") { pairingLink = nil }
                        }
                    }
            }
        }
        .alert(item: pendingDialBinding) { request in
            Alert(
                title: Text("Call from this iPhone?"),
                message: Text("Windows requested a call to \(request.phoneNumber). The system Phone app will open only if you confirm."),
                primaryButton: .default(Text("Call")) {
                    Task { await session.resolveDialRequest(approved: true) }
                },
                secondaryButton: .cancel {
                    Task { await session.resolveDialRequest(approved: false) }
                }
            )
        }
    }

    private var pendingDialBinding: Binding<PendingDialRequest?> {
        Binding(
            get: { session.pendingDialRequest },
            set: { _ in }
        )
    }
}

private struct PairingLink: Identifiable {
    let id = UUID()
    let url: URL
}

#Preview {
    RootView()
        .environmentObject(CompanionSession())
}
