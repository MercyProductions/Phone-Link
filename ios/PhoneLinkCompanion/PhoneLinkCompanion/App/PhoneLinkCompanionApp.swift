import SwiftUI

@main
struct PhoneLinkCompanionApp: App {
    @StateObject private var session = CompanionSession()
    @Environment(\.scenePhase) private var scenePhase

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(session)
                .task {
                    session.start()
                    session.setForegroundActive(scenePhase == .active)
                }
                .onChange(of: scenePhase) { _, phase in
                    if phase == .active { session.setForegroundActive(true) }
                    else if phase == .background { session.setForegroundActive(false) }
                }
        }
    }
}
