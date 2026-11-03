import SwiftUI
import Foundation
import Darwin

@_cdecl("phonelink_runtime_probe_marker")
func phoneLinkRuntimeProbeMarker() -> Int32 { 0x504C }

struct RuntimeImageSnapshot: Codable, Equatable {
    let schemaVersion: Int
    let capturedAt: String
    let scope: String
    let imageUUID: String
    let cpuType: UInt32
    let cpuSubtype: UInt32
    let preferredTextAddress: String
    let loadedHeaderAddress: String
    let textVirtualSize: String
    let slide: String
    let markerRuntimeAddress: String
    let markerImageOffset: String

    // A build/architecture mismatch must never produce a plausible-looking address.
    func resolveTextAddress(_ unslid: UInt64, expectedUUID: String,
                            expectedCpuType: UInt32, expectedCpuSubtype: UInt32) -> UInt64? {
        guard schemaVersion == 1, scope == "companion-own-marker-image",
              let actual = UUID(uuidString: imageUUID), let expected = UUID(uuidString: expectedUUID), actual == expected,
              cpuType == expectedCpuType, cpuSubtype == expectedCpuSubtype,
              let base = Self.hexValue(preferredTextAddress), let size = Self.hexValue(textVirtualSize),
              let loaded = Self.hexValue(loadedHeaderAddress), unslid >= base, unslid - base < size else { return nil }
        let (address, overflow) = loaded.addingReportingOverflow(unslid - base)
        return overflow ? nil : address
    }

    static func hex(_ value: UInt64) -> String { "0x" + String(value, radix: 16) }
    static func hexValue(_ value: String) -> UInt64? {
        guard value.hasPrefix("0x"), value.count > 2 else { return nil }
        return UInt64(value.dropFirst(2), radix: 16)
    }
}

enum RuntimeImageError: Error { case unsupported, invalidHeader, missingMetadata, addressMismatch }

enum RuntimeImageProbe {
    // Inspect only the permanently resident companion code containing our own marker.
    // Do not iterate dyld's mutable image list or dereference unloadable third-party images.
    static func capture() throws -> RuntimeImageSnapshot {
        let marker: @convention(c) () -> Int32 = phoneLinkRuntimeProbeMarker
        let address = unsafeBitCast(marker, to: UnsafeRawPointer.self)
        var info = Dl_info()
        guard dladdr(address, &info) != 0, let header = info.dli_fbase else { throw RuntimeImageError.unsupported }
        let initial = Data(bytes: header, count: 32)
        guard read32(initial, 0) == 0xfeedfacf else { throw RuntimeImageError.unsupported }
        let count = read32(initial, 16)
        let bytes = read32(initial, 20)
        guard count > 0, count <= 4096, bytes <= 1_048_576 else { throw RuntimeImageError.invalidHeader }
        let commands = Data(bytes: header, count: 32 + Int(bytes))
        return try parse(commands, headerAddress: UInt64(UInt(bitPattern: header)),
                         markerAddress: UInt64(UInt(bitPattern: address)))
    }

    static func parse(_ data: Data, headerAddress: UInt64, markerAddress: UInt64) throws -> RuntimeImageSnapshot {
        guard data.count >= 32, read32(data, 0) == 0xfeedfacf,
              [UInt32(2), UInt32(6)].contains(read32(data, 12)) else { throw RuntimeImageError.unsupported }
        let count = read32(data, 16)
        let bytes = read32(data, 20)
        guard count > 0, count <= 4096, bytes <= 1_048_576,
              data.count == 32 + Int(bytes) else { throw RuntimeImageError.invalidHeader }
        var offset = 32
        var uuid: String?
        var text: (base: UInt64, size: UInt64)?
        for _ in 0..<count {
            guard offset <= data.count - 8 else { throw RuntimeImageError.invalidHeader }
            let command = read32(data, offset)
            let size = Int(read32(data, offset + 4))
            guard size >= 8, size % 8 == 0, size <= data.count - offset else { throw RuntimeImageError.invalidHeader }
            if command == 0x1b { // LC_UUID
                guard size == 24, uuid == nil else { throw RuntimeImageError.invalidHeader }
                let raw = Array(data[(offset + 8)..<(offset + 24)])
                uuid = UUID(uuid: (raw[0], raw[1], raw[2], raw[3], raw[4], raw[5], raw[6], raw[7],
                    raw[8], raw[9], raw[10], raw[11], raw[12], raw[13], raw[14], raw[15])).uuidString.lowercased()
            } else if command == 0x19 { // LC_SEGMENT_64
                guard size >= 72 else { throw RuntimeImageError.invalidHeader }
                let name = data[(offset + 8)..<(offset + 24)].prefix { $0 != 0 }
                if String(decoding: name, as: UTF8.self) == "__TEXT" {
                    guard text == nil, read64(data, offset + 40) == 0,
                          read64(data, offset + 48) >= UInt64(data.count),
                          read64(data, offset + 32) >= UInt64(data.count) else { throw RuntimeImageError.invalidHeader }
                    text = (read64(data, offset + 24), read64(data, offset + 32))
                }
            }
            offset += size
        }
        guard offset == data.count, let uuid, let text else { throw RuntimeImageError.missingMetadata }
        let (loadedEnd, loadedOverflow) = headerAddress.addingReportingOverflow(text.size)
        let (_, preferredOverflow) = text.base.addingReportingOverflow(text.size)
        guard !loadedOverflow, !preferredOverflow, markerAddress >= headerAddress, markerAddress < loadedEnd else {
            throw RuntimeImageError.addressMismatch
        }
        let slide = headerAddress >= text.base
            ? RuntimeImageSnapshot.hex(headerAddress - text.base)
            : "-" + RuntimeImageSnapshot.hex(text.base - headerAddress)
        let result = RuntimeImageSnapshot(schemaVersion: 1, capturedAt: PhoneLinkJSON.timestamp(),
            scope: "companion-own-marker-image", imageUUID: uuid,
            cpuType: read32(data, 4), cpuSubtype: read32(data, 8),
            preferredTextAddress: RuntimeImageSnapshot.hex(text.base),
            loadedHeaderAddress: RuntimeImageSnapshot.hex(headerAddress),
            textVirtualSize: RuntimeImageSnapshot.hex(text.size), slide: slide,
            markerRuntimeAddress: RuntimeImageSnapshot.hex(markerAddress),
            markerImageOffset: RuntimeImageSnapshot.hex(markerAddress - headerAddress))
        let (preferredMarker, markerOverflow) = text.base.addingReportingOverflow(markerAddress - headerAddress)
        guard !markerOverflow, result.resolveTextAddress(preferredMarker, expectedUUID: uuid,
            expectedCpuType: result.cpuType, expectedCpuSubtype: result.cpuSubtype) == markerAddress else {
            throw RuntimeImageError.addressMismatch
        }
        return result
    }

    private static func read32(_ data: Data, _ offset: Int) -> UInt32 {
        (0..<4).reduce(UInt32(0)) { $0 | UInt32(data[offset + $1]) << (8 * $1) }
    }
    private static func read64(_ data: Data, _ offset: Int) -> UInt64 {
        UInt64(read32(data, offset)) | UInt64(read32(data, offset + 4)) << 32
    }
}

struct RuntimeImageView: View {
    @Environment(\.scenePhase) private var phase
    @State private var json: String?
    @State private var status = "Not captured. Nothing runs on entry."
    var body: some View {
        Form {
            Section("Companion's own runtime image") {
                Text("Read the loaded code image containing this app's diagnostic marker. This is live process metadata, not a dump of arbitrary memory, other apps, system services or the kernel. No debugger, SSH, developer setting or privilege change.")
                Button("Capture own runtime image") { capture() }
                Text(status).font(.footnote)
            }
            if let json {
                Section("Review before sharing") {
                    Text("Contains this process's live addresses, ASLR slide and code-image UUID. It is not a device identifier, but is sensitive diagnostic information. Addresses apply only to this running process/build. Never reuse them after restart or for a different UUID/architecture.")
                    Text(json).font(.caption.monospaced()).textSelection(.enabled)
                    ShareLink("Share reviewed runtime JSON…", item: json)
                    Text("No automatic Windows transfer. Choose only a destination you intend. Once shared, clearing this screen cannot revoke the recipient's copy.").font(.footnote)
                    Button("Clear snapshot") { clear() }
                }
            }
        }
        .navigationTitle("Own runtime image")
        .onChange(of: phase) { _, value in if value != .active { clear() } }
        .onDisappear { clear() }
    }

    @MainActor private func capture() {
        guard phase == .active else { return }
        do {
            let result = try RuntimeImageProbe.capture()
            let encoder = JSONEncoder(); encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            json = String(decoding: try encoder.encode(result), as: UTF8.self)
            status = "Captured locally. No memory contents or filesystem paths included; nothing sent."
        } catch {
            json = nil; status = "Runtime metadata unavailable or inconsistent. No addresses guessed and no access escalation attempted."
        }
    }
    @MainActor private func clear() { json = nil; status = "Capture a fresh snapshot for this running process." }
}
