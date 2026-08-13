import SwiftUI

@main
struct PhoneLinkCompanionApp: App {
    @StateObject private var session = CompanionSession()

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(session)
                .task {
                    session.start()
                }
        }
    }
}
