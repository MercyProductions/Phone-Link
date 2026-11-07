import SwiftUI
import MessageUI

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
            HealthView()
                .tabItem { Label("Health checks", systemImage: "stethoscope") }
        }
        .tint(.indigo)
        .onOpenURL { url in
            // An external link only opens the review screen; it cannot approve trust.
            guard pairingLink == nil, session.pendingMessageDraft == nil else { return }
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
        .sheet(item: pendingMessageBinding) { draft in
            NativeMessageComposer(input: draft.input) { result in
                Task { await session.finishMessageDraft(id: draft.id, status: result) }
            }
            .interactiveDismissDisabled()
        }
    }

    private var pendingDialBinding: Binding<PendingDialRequest?> {
        Binding(
            get: { session.pendingDialRequest },
            set: { _ in }
        )
    }

    private var pendingMessageBinding: Binding<PendingMessageDraft?> {
        Binding(get: { session.pendingMessageDraft }, set: { _ in })
    }
}

private struct NativeMessageComposer: UIViewControllerRepresentable {
    let input: MessageDraftInput
    let finished: (String) -> Void

    func makeCoordinator() -> Coordinator { Coordinator(finished: finished) }

    func makeUIViewController(context: Context) -> UIViewController {
        guard MFMessageComposeViewController.canSendText() else {
            Task { @MainActor in finished("failed") }
            return UIViewController()
        }
        let controller = MFMessageComposeViewController()
        controller.recipients = [input.recipient]
        controller.body = input.body
        controller.messageComposeDelegate = context.coordinator
        return controller
    }

    func updateUIViewController(_ uiViewController: UIViewController, context: Context) { }

    final class Coordinator: NSObject, MFMessageComposeViewControllerDelegate {
        let finished: (String) -> Void
        init(finished: @escaping (String) -> Void) { self.finished = finished }

        func messageComposeViewController(_ controller: MFMessageComposeViewController, didFinishWith result: MessageComposeResult) {
            switch result {
            case .sent: finished("submitted")
            case .cancelled: finished("declined")
            default: finished("failed")
            }
        }
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
