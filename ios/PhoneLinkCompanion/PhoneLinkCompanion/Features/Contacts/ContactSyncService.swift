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

private struct PendingContactSync: Codable {
    let batches: [ContactSyncBatchPayload]
    let resultingIdentifiers: [String]
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

    private static let previousIdentifiersKey = "contacts.last-synchronized-identifiers.v1"
    private static let sequenceKey = "sync.origin-sequence.v1"
    private let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    func synchronize(using session: CompanionSession) async {
        do {
            state = .requestingPermission
            let accessGranted = try await CNContactStore().requestAccess(for: .contacts)
            guard accessGranted else { throw ContactSyncError.accessDenied }

            state = .reading
            let contacts = try await Task.detached(priority: .userInitiated) {
                try Self.fetchContacts()
            }.value

            let currentIdentifiers = Set(contacts.map(\.sourceContactId))
            var previousIdentifiers = loadPreviousIdentifiers()
            if let pending = try loadPendingSync() {
                try await send(pending, using: session)
                previousIdentifiers = Set(pending.resultingIdentifiers)
                savePreviousIdentifiers(previousIdentifiers)
                advanceSequence(for: pending)
                try clearPendingSync()
            }
            var nextSequence = defaults.object(forKey: Self.sequenceKey) as? Int64
                ?? Int64(defaults.integer(forKey: Self.sequenceKey))
            var events: [ContactSyncEvent] = []
            events.reserveCapacity(contacts.count + previousIdentifiers.subtracting(currentIdentifiers).count)

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
            for identifier in previousIdentifiers.subtracting(currentIdentifiers).sorted() {
                nextSequence += 1
                events.append(ContactSyncEvent(
                    eventId: UUID(),
                    originSequence: nextSequence,
                    occurredAt: PhoneLinkJSON.timestamp(),
                    eventType: "contact.delete",
                    entityId: identifier,
                    payload: ContactPayload(
                        sourceContactId: identifier,
                        displayName: "",
                        givenName: "",
                        familyName: "",
                        organizationName: "",
                        phoneNumbers: [],
                        emailAddresses: []
                    )
                ))
            }

            if events.isEmpty {
                savePreviousIdentifiers(currentIdentifiers)
                state = .complete(0)
                return
            }

            let batches = events.chunked(maximumCount: 100)
                .map { ContactSyncBatchPayload(batchId: UUID(), events: $0) }
            let pending = PendingContactSync(
                batches: batches,
                resultingIdentifiers: currentIdentifiers.sorted()
            )
            try savePendingSync(pending)
            defaults.set(nextSequence, forKey: Self.sequenceKey)
            try await send(pending, using: session)

            savePreviousIdentifiers(currentIdentifiers)
            try clearPendingSync()
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

    private func loadPreviousIdentifiers() -> Set<String> {
        guard let values = defaults.stringArray(forKey: Self.previousIdentifiersKey) else { return [] }
        return Set(values)
    }

    private func savePreviousIdentifiers(_ identifiers: Set<String>) {
        defaults.set(identifiers.sorted(), forKey: Self.previousIdentifiersKey)
    }

    private func send(
        _ pending: PendingContactSync,
        using session: CompanionSession
    ) async throws {
        for (index, batch) in pending.batches.enumerated() {
            guard let finalSequence = batch.events.last?.originSequence else { continue }
            state = .sending(completed: index, total: pending.batches.count)
            try await session.sendAndAwaitSyncAck(
                batchID: batch.batchId,
                payload: batch,
                originSequence: finalSequence
            )
        }
    }

    private func advanceSequence(for pending: PendingContactSync) {
        guard let sequence = pending.batches.last?.events.last?.originSequence else { return }
        let current = defaults.object(forKey: Self.sequenceKey) as? Int64
            ?? Int64(defaults.integer(forKey: Self.sequenceKey))
        defaults.set(max(current, sequence), forKey: Self.sequenceKey)
    }

    private func loadPendingSync() throws -> PendingContactSync? {
        let url = try outboxURL()
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        return try PhoneLinkJSON.decoder.decode(
            PendingContactSync.self,
            from: Data(contentsOf: url)
        )
    }

    private func savePendingSync(_ pending: PendingContactSync) throws {
        let url = try outboxURL()
        let data = try PhoneLinkJSON.encoder.encode(pending)
        try data.write(to: url, options: [.atomic])
        try FileManager.default.setAttributes(
            [.protectionKey: FileProtectionType.complete],
            ofItemAtPath: url.path
        )
    }

    private func clearPendingSync() throws {
        let url = try outboxURL()
        guard FileManager.default.fileExists(atPath: url.path) else { return }
        try FileManager.default.removeItem(at: url)
    }

    private func outboxURL() throws -> URL {
        guard let root = FileManager.default.urls(
            for: .applicationSupportDirectory,
            in: .userDomainMask
        ).first else {
            throw ContactSyncError.outboxUnavailable
        }
        let directory = root.appendingPathComponent("PhoneLink", isDirectory: true)
        try FileManager.default.createDirectory(
            at: directory,
            withIntermediateDirectories: true
        )
        return directory.appendingPathComponent("contacts-outbox-v1.json", isDirectory: false)
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

    var errorDescription: String? {
        switch self {
        case .accessDenied:
            "Contact access is required. You can grant limited or full access in iPhone Settings."
        case .outboxUnavailable:
            "The protected contact synchronization outbox is unavailable."
        }
    }
}
