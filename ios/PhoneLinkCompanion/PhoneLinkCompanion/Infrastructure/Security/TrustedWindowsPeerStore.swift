import Foundation

struct TrustedWindowsPeer: Codable, Equatable {
    let deviceID: UUID
    let identityPublicKey: Data
    let tlsSPKISHA256: Data
    let endpoints: [PairingEndpoint]
    let pairedAt: Date
}

struct TrustedWindowsPeerStore {
    private static let indexAccount = "trusted-windows-peer.index"
    private let keychain: KeychainStore

    init(keychain: KeychainStore = KeychainStore()) {
        self.keychain = keychain
    }

    func save(_ peer: TrustedWindowsPeer) throws {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        try keychain.save(
            encoder.encode(peer),
            for: account(peer.deviceID)
        )

        var identifiers = try loadIndex()
        identifiers.insert(peer.deviceID)
        try keychain.save(encoder.encode(identifiers.sorted { $0.uuidString < $1.uuidString }), for: Self.indexAccount)
    }

    func loadAll() throws -> [TrustedWindowsPeer] {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try loadIndex().compactMap { identifier in
            guard let data = try keychain.data(for: account(identifier)) else { return nil }
            return try decoder.decode(TrustedWindowsPeer.self, from: data)
        }
    }

    func delete(_ deviceID: UUID) throws {
        try keychain.delete(account(deviceID))
        var identifiers = try loadIndex()
        identifiers.remove(deviceID)
        try keychain.save(JSONEncoder().encode(Array(identifiers)), for: Self.indexAccount)
    }

    private func loadIndex() throws -> Set<UUID> {
        guard let data = try keychain.data(for: Self.indexAccount) else { return [] }
        return Set(try JSONDecoder().decode([UUID].self, from: data))
    }

    private func account(_ deviceID: UUID) -> String {
        "trusted-windows-peer.\(deviceID.uuidString.lowercased())"
    }
}
