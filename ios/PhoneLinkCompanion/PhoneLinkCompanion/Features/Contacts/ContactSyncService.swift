@preconcurrency import Contacts
import Foundation

struct ContactMethodPayload: Codable, Sendable {
    let label: String
    let displayValue: String
    let normalizedValue: String
}

struct ContactPayload: Codable, Sendable {
    let sourceContactId: String
    let displayName: String
    let givenName: String
    let familyName: String
    let organizationName: String
    let phoneNumbers: [ContactMethodPayload]
    let emailAddresses: [ContactMethodPayload]
}

struct ContactSyncEvent: Codable, Sendable {
    let eventId: UUID
    let originSequence: Int64
    let occurredAt: String
    let eventType: String
    let entityId: String
    let payload: ContactPayload
}

struct ContactSyncBatchPayload: Codable, Sendable {
    let batchId: UUID
    let events: [ContactSyncEvent]
}

struct PendingContactSync: Codable {
    let batches: [ContactSyncBatchPayload]
    let resultingIdentifiers: [String]
}

struct ContactSyncState: Codable {
    let peerDeviceID: UUID
    var lastSequence: Int64
    var pending: PendingContactSync?
}

struct ContactSyncStateStore {
    let directory: URL

    static func defaultStore() throws -> ContactSyncStateStore {
        guard let root = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first else {
            throw ContactSyncError.outboxUnavailable
        }
        return ContactSyncStateStore(directory: root.appendingPathComponent("PhoneLink/ContactSync", isDirectory: true))
    }

    func load(peerID: UUID) throws -> ContactSyncState {
        let url = stateURL(peerID)
        guard FileManager.default.fileExists(atPath: url.path) else {
            return ContactSyncState(peerDeviceID: peerID, lastSequence: 0, pending: nil)
        }
        let state = try PhoneLinkJSON.decoder.decode(ContactSyncState.self, from: Data(contentsOf: url))
        guard state.peerDeviceID == peerID, state.lastSequence >= 0 else { throw ContactSyncError.invalidState }
        return state
    }

    func save(_ state: ContactSyncState) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try PhoneLinkJSON.encoder.encode(state).write(to: stateURL(state.peerDeviceID), options: [.atomic, .completeFileProtection])
    }

    func remove(peerID: UUID) throws {
        let url = stateURL(peerID)
        if FileManager.default.fileExists(atPath: url.path) { try FileManager.default.removeItem(at: url) }
    }

    func retireLegacyState() throws {
        let legacy = directory.deletingLastPathComponent().appendingPathComponent("contacts-outbox-v1.json")
        if FileManager.default.fileExists(atPath: legacy.path) { try FileManager.default.removeItem(at: legacy) }
        UserDefaults.standard.removeObject(forKey: "contacts.last-synchronized-identifiers.v1")
        UserDefaults.standard.removeObject(forKey: "sync.origin-sequence.v1")
    }

    private func stateURL(_ peerID: UUID) -> URL {
        directory.appendingPathComponent("\(peerID.uuidString.lowercased()).json")
    }
}

extension PendingContactSync {
    func recovered(after checkpoint: Int64, contacts: [ContactPayload]) -> PendingContactSync? {
        let authorized = Dictionary(contacts.map { ($0.sourceContactId, $0) }, uniquingKeysWith: { _, last in last })
        let remaining = batches.flatMap(\.events).filter { $0.originSequence > checkpoint }
        guard remaining.first?.originSequence == checkpoint + 1 else { return nil }
        let events = remaining.map { old in
            let current = authorized[old.entityId]
            return ContactSyncEvent(
                eventId: UUID(), originSequence: old.originSequence,
                occurredAt: PhoneLinkJSON.timestamp(), eventType: current == nil ? "contact.delete" : "contact.upsert",
                entityId: old.entityId, payload: current ?? ContactPayload(
                    sourceContactId: old.entityId, displayName: "", givenName: "", familyName: "",
                    organizationName: "", phoneNumbers: [], emailAddresses: []
                )
            )
        }
        return PendingContactSync(batches: events.chunked(maximumCount: 100).map {
            ContactSyncBatchPayload(batchId: UUID(), events: $0)
        }, resultingIdentifiers: contacts.map(\.sourceContactId).sorted())
    }
}

@MainActor
final class ContactSyncService: ObservableObject {
    enum State: Equatable {
        case idle
        case requestingPermission
        case reading
        case sending(completed: Int, total: Int)
        case complete(Int)
        case failed(String)
    }

    @Published private(set) var state: State = .idle

    func synchronize(using session: CompanionSession) async {
        do {
            let target = try session.beginContactSync()
            defer { session.endContactSync() }
            state = .requestingPermission
            let accessGranted = try await CNContactStore().requestAccess(for: .contacts)

            state = .reading
            let contacts: [ContactPayload]
            if accessGranted {
                contacts = try await Task.detached(priority: .userInitiated) { try Self.fetchContacts() }.value
            } else {
                contacts = []
            }

            let currentIdentifiers = Set(contacts.map(\.sourceContactId))
            try session.requireConnection(target)
            let checkpoint = try await session.contactCheckpoint()
            try session.requireConnection(target)
            try await session.reconcileContacts(currentIdentifiers.sorted())
            try session.requireConnection(target)
            let store = try ContactSyncStateStore.defaultStore()
            var saved = try store.load(peerID: target.peerDeviceID)
            let recovered = saved.pending?.recovered(after: checkpoint, contacts: contacts)
            saved.lastSequence = checkpoint
            saved.pending = recovered
            try store.save(saved)
            try store.retireLegacyState()
            if let pending = recovered {
                try await send(pending, using: session, target: target)
                saved.lastSequence = pending.batches.last?.events.last?.originSequence ?? checkpoint
                saved.pending = nil
                try store.save(saved)
            }
            var nextSequence = saved.lastSequence
            var events: [ContactSyncEvent] = []
            events.reserveCapacity(contacts.count)

            for contact in contacts {
                nextSequence += 1
                events.append(ContactSyncEvent(
                    eventId: UUID(),
                    originSequence: nextSequence,
                    occurredAt: PhoneLinkJSON.timestamp(),
                    eventType: "contact.upsert",
                    entityId: contact.sourceContactId,
                    payload: contact
                ))
            }
            if events.isEmpty {
                state = accessGranted ? .complete(0) : .failed("Contact permission was denied. The Windows contact cache was cleared.")
                return
            }

            let batches = events.chunked(maximumCount: 100)
                .map { ContactSyncBatchPayload(batchId: UUID(), events: $0) }
            let pending = PendingContactSync(
                batches: batches,
                resultingIdentifiers: currentIdentifiers.sorted()
            )
            saved.pending = pending
            try store.save(saved)
            try await send(pending, using: session, target: target)
            saved.lastSequence = nextSequence
            saved.pending = nil
            try store.save(saved)
            state = .complete(contacts.count)
        } catch {
            state = .failed(error.localizedDescription)
        }
    }

    nonisolated private static func fetchContacts() throws -> [ContactPayload] {
        let formatterDescriptor = CNContactFormatter.descriptorForRequiredKeys(for: .fullName)
        let keys: [CNKeyDescriptor] = [
            CNContactIdentifierKey as CNKeyDescriptor,
            CNContactGivenNameKey as CNKeyDescriptor,
            CNContactFamilyNameKey as CNKeyDescriptor,
            CNContactOrganizationNameKey as CNKeyDescriptor,
            CNContactPhoneNumbersKey as CNKeyDescriptor,
            CNContactEmailAddressesKey as CNKeyDescriptor,
            formatterDescriptor,
        ]
        let request = CNContactFetchRequest(keysToFetch: keys)
        request.sortOrder = .userDefault
        request.unifyResults = true
        var results: [ContactPayload] = []
        try CNContactStore().enumerateContacts(with: request) { contact, _ in
            let phones = contact.phoneNumbers.map { labeled in
                let display = labeled.value.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
                return ContactMethodPayload(
                    label: localizedLabel(labeled.label),
                    displayValue: display,
                    normalizedValue: normalizePhone(display)
                )
            }.filter { !$0.normalizedValue.isEmpty }
            let emails = contact.emailAddresses.map { labeled in
                let display = String(labeled.value).trimmingCharacters(in: .whitespacesAndNewlines)
                return ContactMethodPayload(
                    label: localizedLabel(labeled.label),
                    displayValue: display,
                    normalizedValue: display.lowercased()
                )
            }.filter { !$0.normalizedValue.isEmpty }
            let formattedName = CNContactFormatter.string(from: contact, style: .fullName)?
                .trimmingCharacters(in: .whitespacesAndNewlines)
            let fallback = contact.organizationName.isEmpty
                ? phones.first?.displayValue ?? emails.first?.displayValue ?? "Unnamed contact"
                : contact.organizationName

            results.append(ContactPayload(
                sourceContactId: String(contact.identifier.prefix(500)),
                displayName: String((formattedName?.isEmpty == false ? formattedName! : fallback).prefix(500)),
                givenName: String(contact.givenName.prefix(200)),
                familyName: String(contact.familyName.prefix(200)),
                organizationName: String(contact.organizationName.prefix(300)),
                phoneNumbers: Array(phones.prefix(50)),
                emailAddresses: Array(emails.prefix(50))
            ))
        }
        return results
    }

    nonisolated private static func localizedLabel(_ label: String?) -> String {
        guard let label else { return "" }
        return String(CNLabeledValue<NSString>.localizedString(forLabel: label).prefix(100))
    }

    nonisolated private static func normalizePhone(_ value: String) -> String {
        var normalized = ""
        for character in value {
            if character.isNumber || (character == "+" && normalized.isEmpty) {
                normalized.append(character)
            }
        }
        return normalized
    }

    private func send(
        _ pending: PendingContactSync,
        using session: CompanionSession,
        target: ConnectedWindowsSession
    ) async throws {
        for (index, batch) in pending.batches.enumerated() {
            try session.requireConnection(target)
            guard let finalSequence = batch.events.last?.originSequence else { continue }
            state = .sending(completed: index, total: pending.batches.count)
            try await session.sendAndAwaitSyncAck(
                batchID: batch.batchId,
                payload: batch,
                originSequence: finalSequence
            )
            try session.requireConnection(target)
        }
    }
}

private extension Array {
    func chunked(maximumCount: Int) -> [[Element]] {
        guard maximumCount > 0 else { return [] }
        return stride(from: 0, to: count, by: maximumCount).map {
            Array(self[$0..<Swift.min($0 + maximumCount, count)])
        }
    }
}

enum ContactSyncError: LocalizedError {
    case accessDenied
    case outboxUnavailable
    case alreadySynchronizing
    case invalidState

    var errorDescription: String? {
        switch self {
        case .accessDenied:
            "Contact access is required. You can grant limited or full access in iPhone Settings."
        case .outboxUnavailable:
            "The protected contact synchronization outbox is unavailable."
        case .alreadySynchronizing:
            "Contact synchronization is already running."
        case .invalidState:
            "The contact synchronization state belongs to another PC or is invalid."
        }
    }
}
