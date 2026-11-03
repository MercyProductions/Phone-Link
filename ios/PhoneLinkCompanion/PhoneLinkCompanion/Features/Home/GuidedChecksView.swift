import SwiftUI
import AVFoundation

// Only occupied cells are retained. No raw touch paths, pressure, identifiers or telemetry.
struct TouchCoverage: Equatable {
    static let columns = 6
    static let rows = 8
    private(set) var cells: Set<Int> = []

    mutating func record(_ point: CGPoint, in size: CGSize) {
        guard size.width.isFinite, size.height.isFinite, size.width > 0, size.height > 0,
              point.x.isFinite, point.y.isFinite,
              point.x >= 0, point.y >= 0, point.x <= size.width, point.y <= size.height else { return }
        let column = min(Self.columns - 1, Int(point.x / size.width * CGFloat(Self.columns)))
        let row = min(Self.rows - 1, Int(point.y / size.height * CGFloat(Self.rows)))
        cells.insert(row * Self.columns + column)
    }
}

enum CheckObservation: String, CaseIterable, Identifiable {
    case notChecked = "Not checked"
    case noticed = "Noticed as expected"
    case concern = "Noticed a concern"
    case unsure = "Unsure"
    var id: String { rawValue }
}

enum CheckTone {
    // 0.4 s, mono 16-bit PCM WAV, 440 Hz, 15% peak with 10 ms edge fades.
    static func wav() -> Data {
        let rate = 22_050
        let frames = rate * 2 / 5
        var data = Data()
        func text(_ value: String) { data.append(contentsOf: value.utf8) }
        func u16(_ value: UInt16) { data.append(UInt8(value & 255)); data.append(UInt8(value >> 8)) }
        func u32(_ value: UInt32) { u16(UInt16(value & 65535)); u16(UInt16(value >> 16)) }
        text("RIFF"); u32(UInt32(36 + frames * 2)); text("WAVEfmt "); u32(16)
        u16(1); u16(1); u32(UInt32(rate)); u32(UInt32(rate * 2)); u16(2); u16(16)
        text("data"); u32(UInt32(frames * 2))
        for index in 0..<frames {
            let edge = min(1.0, Double(min(index, frames - 1 - index)) / Double(rate / 100))
            let value = sin(2 * Double.pi * 440 * Double(index) / Double(rate)) * 0.15 * edge
            u16(UInt16(bitPattern: Int16((value * Double(Int16.max)).rounded())))
        }
        return data
    }
}

struct GuidedChecksView: View {
    @Environment(\.scenePhase) private var phase
    @State private var coverage = TouchCoverage()
    @State private var soundObservation = CheckObservation.notChecked
    @State private var hapticObservation = CheckObservation.notChecked
    @State private var hapticTrigger = 0
    @State private var soundAttempted = false
    @State private var hapticAttempted = false
    @State private var player: AVAudioPlayer?
    @State private var soundStatus = "No tone requested."
    @State private var displayPresented = false

    var body: some View {
        Form {
            Section("Guided checks — this iPhone only") {
                Text("These checks do not repair hardware or certify it healthy. Only touch events are observed by the app; sound, vibration and display appearance need your own judgment. Nothing here is sent to Windows or saved to disk.")
            }
            Section("Touch response in the test area") {
                Text("Slowly drag a finger through every cell. A filled cell means the app received a gesture location there. Unfilled cells are untested, not failed. This is not a full-screen, multi-touch, pressure or calibration test.")
                GeometryReader { geometry in
                    Canvas { context, size in
                        let width = size.width / CGFloat(TouchCoverage.columns)
                        let height = size.height / CGFloat(TouchCoverage.rows)
                        for cell in 0..<(TouchCoverage.columns * TouchCoverage.rows) {
                            let rect = CGRect(x: CGFloat(cell % TouchCoverage.columns) * width + 1,
                                y: CGFloat(cell / TouchCoverage.columns) * height + 1,
                                width: max(0, width - 2), height: max(0, height - 2))
                            context.fill(Path(rect), with: .color(coverage.cells.contains(cell) ? .green : .gray.opacity(0.3)))
                            if coverage.cells.contains(cell) {
                                context.draw(Text("✓").foregroundColor(.black), at: CGPoint(x: rect.midX, y: rect.midY))
                            }
                        }
                    }
                    .contentShape(Rectangle())
                    .gesture(DragGesture(minimumDistance: 0, coordinateSpace: .local)
                        .onChanged { value in
                            guard phase == .active else { return }
                            coverage.record(value.location, in: geometry.size)
                        })
                    .accessibilityLabel("Touch test area; physical dragging required. \(coverage.cells.count) of 48 cells observed.")
                }.frame(height: 320)
                Text("Observed gesture locations in \(coverage.cells.count)/48 cells. No hardware pass/fail inferred.")
                Button("Clear touch coverage") { coverage = TouchCoverage() }
                Text("If VoiceOver or another accessibility gesture mode is active, gesture delivery may differ. Do not disable an accessibility aid just to complete this check.")
                    .font(.footnote)
            }
            Section("Display appearance") {
                Text("Open manually advanced solid colors. There is no automatic flashing or brightness change. Stop if uncomfortable. Inspect only the visible test area; controls and system regions are not tested.")
                Button("Open display colors") { displayPresented = true }
            }
            Section("Sound — current audio route") {
                Text("Lower your volume first and check where audio is routed. The short tone may play through headphones or Bluetooth, not the iPhone speaker, and may interrupt other audio. Silence can result from routing, silent mode or volume; it does not prove a broken speaker. No recording or volume change.")
                Button("Play one short tone") { playTone() }
                Button("Stop tone") { stopTone() }
                Text(soundStatus).font(.footnote)
                Picker("Your sound observation", selection: $soundObservation) {
                    ForEach(CheckObservation.allCases) { Text($0.rawValue).tag($0) }
                }.disabled(!soundAttempted)
            }
            Section("Haptic response") {
                Text("Request one impact, then record what you felt. Device support, settings and system decisions may suppress it; an absent response is not a hardware diagnosis.")
                Button("Request one haptic impact") {
                    guard phase == .active else { return }
                    hapticAttempted = true; hapticObservation = .notChecked; hapticTrigger += 1
                }
                .sensoryFeedback(.impact, trigger: hapticTrigger)
                Picker("Your haptic observation", selection: $hapticObservation) {
                    ForEach(CheckObservation.allCases) { Text($0.rawValue).tag($0) }
                }.disabled(!hapticAttempted)
            }
            Section("Your observations, not sensor measurements") {
                Text("Sound: \(soundObservation.rawValue). Haptic: \(hapticObservation.rawValue). A noticed concern needs repeatable checks and appropriate support, not an automatic reset or part replacement.")
                Button("Clear these checks") { clear() }
            }
        }
        .navigationTitle("Guided device checks")
        .fullScreenCover(isPresented: $displayPresented) { DisplayColorsView() }
        .onChange(of: phase) { _, value in
            if value != .active { displayPresented = false; clear() }
        }
        .onDisappear { stopTone() }
    }

    @MainActor private func playTone() {
        guard phase == .active else { return }
        stopTone(); soundAttempted = true; soundObservation = .notChecked
        do {
            let next = try AVAudioPlayer(data: CheckTone.wav())
            next.volume = 0.5
            player = next
            soundStatus = next.play()
                ? "Playback requested on the current system route. This does not confirm that sound was heard."
                : "Playback did not start. No speaker fault is inferred."
        } catch { soundStatus = "Tone unavailable. No speaker fault is inferred." }
    }

    @MainActor private func stopTone() { player?.stop(); player = nil }
    @MainActor private func clear() {
        stopTone(); coverage = TouchCoverage(); soundObservation = .notChecked; hapticObservation = .notChecked
        soundAttempted = false; hapticAttempted = false; soundStatus = "No tone requested."
    }
}

private struct DisplayColorsView: View {
    @Environment(\.dismiss) private var dismiss
    @State private var index = 0
    @State private var observation = CheckObservation.notChecked
    private let colors: [(String, Color)] = [("Black", .black), ("White", .white), ("Red", .red), ("Green", .green), ("Blue", .blue)]
    var body: some View {
        ZStack {
            colors[index].1.ignoresSafeArea()
            VStack {
                VStack(spacing: 12) {
                    Text("\(colors[index].0) — \(index + 1)/\(colors.count)")
                    Text("Inspect the exposed area. This is your observation, not a measured panel test.").font(.footnote)
                    Picker("Your observation for this color", selection: $observation) {
                        ForEach(CheckObservation.allCases) { Text($0.rawValue).tag($0) }
                    }
                    Text("Current color: \(observation.rawValue)").font(.footnote)
                    HStack {
                        Button("Next color") { index = (index + 1) % colors.count; observation = .notChecked }
                        Spacer()
                        Button("Close") { dismiss() }
                    }
                }.padding().background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16))
                Spacer()
            }.padding()
        }
    }
}
