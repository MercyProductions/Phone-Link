import SwiftUI
import CoreMotion

struct MotionAxisBounds: Codable, Equatable, Sendable {
    var minimum: Double
    var maximum: Double
    mutating func include(_ value: Double) {
        minimum = min(minimum, value); maximum = max(maximum, value)
    }
}

struct MotionSampleSummary: Codable, Equatable, Sendable {
    var sampleCount: Int
    var x: MotionAxisBounds
    var y: MotionAxisBounds
    var z: MotionAxisBounds
}

// Aggregate only: no raw path, absolute sensor uptime or sample array is retained/exported.
struct MotionSampleAccumulator {
    static let maximumSamples = 120
    private var lastTimestamp: TimeInterval?
    private(set) var summary: MotionSampleSummary?

    init(after timestamp: TimeInterval? = nil) {
        lastTimestamp = timestamp.flatMap { $0.isFinite && $0 >= 0 ? $0 : nil }
    }

    mutating func record(timestamp: TimeInterval, x: Double, y: Double, z: Double) {
        guard timestamp.isFinite, timestamp >= 0, x.isFinite, y.isFinite, z.isFinite,
              lastTimestamp.map({ timestamp > $0 }) ?? true,
              (summary?.sampleCount ?? 0) < Self.maximumSamples else { return }
        lastTimestamp = timestamp
        if var value = summary {
            value.sampleCount += 1
            value.x.include(x); value.y.include(y); value.z.include(z)
            summary = value
        } else {
            summary = MotionSampleSummary(sampleCount: 1,
                x: MotionAxisBounds(minimum: x, maximum: x),
                y: MotionAxisBounds(minimum: y, maximum: y),
                z: MotionAxisBounds(minimum: z, maximum: z))
        }
    }
}

enum MotionCheckOutcome: String, Codable, Sendable {
    case reported = "Reported"
    case unavailable = "Unavailable"
    case unknown = "Unknown"

    static func classify(available: Bool, summary: MotionSampleSummary?) -> Self {
        !available ? .unavailable : summary == nil ? .unknown : .reported
    }
}

struct MotionSensorObservation: Codable, Sendable {
    let sensor: String
    let source: String
    let units: String
    let outcome: MotionCheckOutcome
    let summary: MotionSampleSummary?
}

struct MotionDiagnosticReport: Codable, Sendable {
    let schemaVersion: Int
    let capturedAt: String
    let method: String
    let limitation: String
    let sensors: [MotionSensorObservation]
}

@MainActor
final class MotionDiagnosticController: ObservableObject {
    @Published private(set) var running = false
    @Published private(set) var report: MotionDiagnosticReport?
    @Published private(set) var status = "No sensors have been started."
    private let manager = CMMotionManager()
    private var task: Task<Void, Never>?
    private var runID = UUID()
    private var acceleration = MotionSampleAccumulator()
    private var rotation = MotionSampleAccumulator()
    private var magneticField = MotionSampleAccumulator()
    private var availability = (acceleration: false, rotation: false, magneticField: false)

    func start() {
        guard !running else { return }
        clear()
        running = true
        status = "Sampling for five seconds. Keep the phone secure; gently tilt it only if comfortable. No shaking required."
        availability = (manager.isAccelerometerAvailable, manager.isGyroAvailable, manager.isMagnetometerAvailable)
        // Exclude Core Motion's cached value from a previous run if updates never resume.
        acceleration = MotionSampleAccumulator(after: manager.accelerometerData?.timestamp)
        rotation = MotionSampleAccumulator(after: manager.gyroData?.timestamp)
        magneticField = MotionSampleAccumulator(after: manager.magnetometerData?.timestamp)
        manager.accelerometerUpdateInterval = 0.05
        manager.gyroUpdateInterval = 0.05
        manager.magnetometerUpdateInterval = 0.05
        if availability.acceleration { manager.startAccelerometerUpdates() }
        if availability.rotation { manager.startGyroUpdates() }
        if availability.magneticField { manager.startMagnetometerUpdates() }
        let id = runID
        task = Task { @MainActor [weak self] in
            let clock = ContinuousClock()
            let deadline = clock.now.advanced(by: .seconds(5))
            while clock.now < deadline {
                do { try await Task.sleep(for: .milliseconds(50)) } catch { return }
                if clock.now >= deadline { break }
                guard let self, self.running, self.runID == id, !Task.isCancelled else { return }
                self.poll()
            }
            guard let self, self.running, self.runID == id, !Task.isCancelled else { return }
            self.finish()
        }
    }

    private func poll() {
        if let data = manager.accelerometerData {
            acceleration.record(timestamp: data.timestamp, x: data.acceleration.x, y: data.acceleration.y, z: data.acceleration.z)
        }
        if let data = manager.gyroData {
            rotation.record(timestamp: data.timestamp, x: data.rotationRate.x, y: data.rotationRate.y, z: data.rotationRate.z)
        }
        if let data = manager.magnetometerData {
            magneticField.record(timestamp: data.timestamp, x: data.magneticField.x, y: data.magneticField.y, z: data.magneticField.z)
        }
    }

    private func stopServices() {
        manager.stopAccelerometerUpdates(); manager.stopGyroUpdates(); manager.stopMagnetometerUpdates()
    }

    private func finish() {
        stopServices()
        running = false; task = nil
        report = MotionDiagnosticReport(schemaVersion: 1, capturedAt: PhoneLinkJSON.timestamp(),
            method: "User-started five-second foreground Core Motion capture, polled approximately every 50 ms; only fresh finite samples counted.",
            limitation: "Reported means samples received, not calibrated or healthy hardware. Unavailable is an API availability observation, not proof that a sensor is absent. Unknown means no valid fresh samples; permission, system policy and device state may affect delivery. No repair, calibration, location, heading, activity history or Windows transmission.",
            sensors: [
                observation("Accelerometer", "CMMotionManager.accelerometerData", "g (includes gravity)", availability.acceleration, acceleration),
                observation("Gyroscope", "CMMotionManager.gyroData", "radians/second", availability.rotation, rotation),
                observation("Magnetometer", "CMMotionManager.magnetometerData", "microtesla (uncalibrated field)", availability.magneticField, magneticField),
            ])
        status = "Capture finished and sensor updates stopped. Review the summaries; no hardware pass/fail or repair is inferred."
    }

    private func observation(_ sensor: String, _ source: String, _ units: String, _ available: Bool,
        _ accumulator: MotionSampleAccumulator) -> MotionSensorObservation {
        MotionSensorObservation(sensor: sensor, source: source, units: units,
            outcome: .classify(available: available, summary: accumulator.summary),
            summary: available ? accumulator.summary : nil)
    }

    func clear() {
        runID = UUID(); task?.cancel(); task = nil
        stopServices(); running = false; report = nil
        acceleration = MotionSampleAccumulator(); rotation = MotionSampleAccumulator(); magneticField = MotionSampleAccumulator()
        status = "Stopped and cleared. Start a fresh foreground check when ready."
    }

    var reportJSON: String? {
        guard let report else { return nil }
        let encoder = JSONEncoder(); encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(report) else { return nil }
        return String(data: data, encoding: .utf8)
    }
}

struct MotionChecksView: View {
    @Environment(\.scenePhase) private var phase
    @StateObject private var controller = MotionDiagnosticController()

    var body: some View {
        Form {
            Section("Motion sensors — explicit foreground check") {
                Text("Reads accelerometer, gyroscope and magnetometer summaries through public Core Motion APIs. iOS may control access or show a permission prompt; you decide. No location permission or motion/activity history is requested. Nothing starts until you press the button.")
                Button("Start five-second sensor check") {
                    guard phase == .active else { return }
                    controller.start()
                }.disabled(controller.running)
                Button("Stop and clear") { controller.clear() }
                if controller.running { ProgressView("Sampling…") }
                Text(controller.status)
            }
            if let report = controller.report {
                Section("Review local observations") {
                    Text("Captured: \(report.capturedAt)").font(.caption)
                    ForEach(report.sensors, id: \.sensor) { observation in
                        VStack(alignment: .leading, spacing: 6) {
                            Text("\(observation.sensor): \(observation.outcome.rawValue)").font(.headline)
                            Text("Source: \(observation.source) · \(observation.units)").font(.caption)
                            if let summary = observation.summary {
                                Text("\(summary.sampleCount) fresh samples\nX: \(bounds(summary.x))\nY: \(bounds(summary.y))\nZ: \(bounds(summary.z))")
                                    .monospacedDigit().textSelection(.enabled)
                            } else { Text("No valid sample range to report. No sensor failure inferred.") }
                        }
                    }
                    Text(report.method).font(.footnote)
                    Text(report.limitation).font(.footnote)
                }
                Section("Optional export after review") {
                    if let json = controller.reportJSON {
                        ShareLink("Share reviewed summary JSON", item: json)
                    }
                    Text("Only summary ranges, sample counts, sources, outcomes and capture time are exported. No raw sample path, device identifier or uptime. Nothing is sent to Windows automatically. Leaving or backgrounding clears this page; copies you explicitly share remain with the destination.")
                        .font(.footnote)
                }
            }
        }
        .navigationTitle("Motion sensor check")
        .onChange(of: phase) { _, value in if value != .active { controller.clear() } }
        .onDisappear { controller.clear() }
    }

    private func bounds(_ value: MotionAxisBounds) -> String {
        String(format: "%.4f … %.4f", value.minimum, value.maximum)
    }
}
