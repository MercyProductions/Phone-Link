import SwiftUI

struct SettingsView: View {
    @AppStorage("privacy.notificationPreviews") private var notificationPreviews = false
    @AppStorage("privacy.diagnostics") private var diagnostics = false

    var body: some View {
        NavigationStack {
            Form {
                Section("Privacy") {
                    Toggle("Share notification previews", isOn: $notificationPreviews)
                    Toggle("Keep local diagnostics", isOn: $diagnostics)
                } footer: {
                    Text("Diagnostics never include message bodies, contact fields, notification text, or cryptographic keys.")
                }

                Section("Connection") {
                    LabeledContent("Preferred transport", value: "Local Wi-Fi")
                    LabeledContent("Bluetooth", value: "Wake and capability detection")
                }

                Section("About") {
                    LabeledContent("Protocol", value: "Phone Link v1")
                    LabeledContent("Data path", value: "Direct to your PC")
                }
            }
            .navigationTitle("Settings")
        }
    }
}
