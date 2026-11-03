import Foundation
import XCTest
@testable import PhoneLinkCompanion

final class MotionDiagnosticTests: XCTestCase {
    func testFreshFiniteSamplesProduceCorrectAxisRanges() {
        var accumulator = MotionSampleAccumulator()
        accumulator.record(timestamp: 1, x: -1, y: 2, z: 3)
        accumulator.record(timestamp: 2, x: 4, y: -5, z: 6)
        XCTAssertEqual(accumulator.summary?.sampleCount, 2)
        XCTAssertEqual(accumulator.summary?.x, MotionAxisBounds(minimum: -1, maximum: 4))
        XCTAssertEqual(accumulator.summary?.y, MotionAxisBounds(minimum: -5, maximum: 2))
        XCTAssertEqual(accumulator.summary?.z, MotionAxisBounds(minimum: 3, maximum: 6))
    }

    func testRepeatedOutOfOrderInvalidAndNonFiniteSamplesDoNotInflateEvidence() {
        var accumulator = MotionSampleAccumulator()
        accumulator.record(timestamp: 2, x: 1, y: 2, z: 3)
        accumulator.record(timestamp: 2, x: 99, y: 99, z: 99)
        accumulator.record(timestamp: 1, x: 99, y: 99, z: 99)
        accumulator.record(timestamp: -1, x: 99, y: 99, z: 99)
        accumulator.record(timestamp: .infinity, x: 1, y: 2, z: 3)
        accumulator.record(timestamp: 3, x: .nan, y: 2, z: 3)
        accumulator.record(timestamp: 3, x: 1, y: .infinity, z: 3)
        accumulator.record(timestamp: 3, x: 1, y: 2, z: -.infinity)
        XCTAssertEqual(accumulator.summary?.sampleCount, 1)
        XCTAssertEqual(accumulator.summary?.x.maximum, 1)
        accumulator.record(timestamp: 3, x: 4, y: 5, z: 6)
        XCTAssertEqual(accumulator.summary?.sampleCount, 2)
    }

    func testCachedPreRunSampleDoesNotCountAsFreshDelivery() {
        var accumulator = MotionSampleAccumulator(after: 42)
        accumulator.record(timestamp: 42, x: 1, y: 2, z: 3)
        accumulator.record(timestamp: 41, x: 1, y: 2, z: 3)
        XCTAssertNil(accumulator.summary)
        accumulator.record(timestamp: 43, x: 1, y: 2, z: 3)
        XCTAssertEqual(accumulator.summary?.sampleCount, 1)
    }

    func testEvidenceIsBounded() {
        var accumulator = MotionSampleAccumulator()
        for index in 0..<1_000 {
            accumulator.record(timestamp: Double(index), x: Double(index), y: 0, z: 0)
        }
        XCTAssertEqual(accumulator.summary?.sampleCount, MotionSampleAccumulator.maximumSamples)
        XCTAssertEqual(accumulator.summary?.x.maximum, Double(MotionSampleAccumulator.maximumSamples - 1))
    }

    func testNoSamplesAreUnknownNotHardwareFailure() {
        XCTAssertEqual(MotionCheckOutcome.classify(available: true, summary: nil), .unknown)
        XCTAssertEqual(MotionCheckOutcome.classify(available: false, summary: nil), .unavailable)
        var accumulator = MotionSampleAccumulator()
        accumulator.record(timestamp: 1, x: 0, y: 0, z: 0)
        XCTAssertEqual(MotionCheckOutcome.classify(available: true, summary: accumulator.summary), .reported)
        XCTAssertEqual(MotionCheckOutcome.classify(available: false, summary: accumulator.summary), .unavailable)
    }

    func testSummaryExportContainsNoRawTimestampOrSampleArray() throws {
        var accumulator = MotionSampleAccumulator()
        accumulator.record(timestamp: 123456.789, x: 1, y: 2, z: 3)
        let report = MotionDiagnosticReport(schemaVersion: 1, capturedAt: "2026-11-03T00:00:00Z",
            method: "fixture", limitation: "No hardware certification", sensors: [
                MotionSensorObservation(sensor: "Accelerometer", source: "CMMotionManager.accelerometerData",
                    units: "g", outcome: .reported, summary: accumulator.summary)])
        let data = try JSONEncoder().encode(report)
        let text = try XCTUnwrap(String(data: data, encoding: .utf8))
        XCTAssertFalse(text.contains("123456"))
        XCTAssertFalse(text.contains("timestamp"))
        let decoded = try JSONDecoder().decode(MotionDiagnosticReport.self, from: data)
        XCTAssertEqual(decoded.sensors.first?.summary, accumulator.summary)
    }
}
