import CryptoKit
import Foundation

struct CompanionDeviceIdentity {
    let privateKey: Curve25519.Signing.PrivateKey

    var publicKey: Data {
        privateKey.publicKey.rawRepresentation
    }
}

struct DeviceIdentityStore {
    private static let keychainAccount = "device-identity.ed25519"
    private static let deviceIDAccount = "device-identity.uuid"
    private let keychain: KeychainStore

    init(keychain: KeychainStore = KeychainStore()) {
        self.keychain = keychain
    }

    func loadOrCreate() throws -> CompanionDeviceIdentity {
        if let storedKey = try keychain.data(for: Self.keychainAccount) {
            return CompanionDeviceIdentity(
                privateKey: try Curve25519.Signing.PrivateKey(rawRepresentation: storedKey)
            )
        }

        let newKey = Curve25519.Signing.PrivateKey()
        try keychain.save(newKey.rawRepresentation, for: Self.keychainAccount)
        return CompanionDeviceIdentity(privateKey: newKey)
    }

    func loadOrCreateDeviceID() throws -> UUID {
        if let storedID = try keychain.data(for: Self.deviceIDAccount),
           let storedValue = String(data: storedID, encoding: .utf8),
           let deviceID = UUID(uuidString: storedValue) {
            return deviceID
        }

        let deviceID = UUID()
        try keychain.save(Data(deviceID.uuidString.lowercased().utf8), for: Self.deviceIDAccount)
        return deviceID
    }
}
