import SwiftUI

struct RootView: View {
    @EnvironmentObject private var session: CompanionSession

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

#Preview {
    RootView()
        .environmentObject(CompanionSession())
}
