# Phone Link SQLite schema v1

Status: **frozen for Phase 3 implementation**  
Database owner: Windows application  
Engine: SQLite 3.53.4 through `Microsoft.Data.Sqlite.Core` and SQLite3 Multiple Ciphers 2.4.0

## Storage boundary

The Windows database may contain only:

- trusted-device public metadata (never private keys or bearer secrets);
- contacts explicitly authorized on iPhone or made available through opted-in PBAP;
- product-owned conversations and messages;
- EU carrier events legitimately received while the companion is the configured default app;
- opted-in PBAP call history;
- product-owned notification events;
- sync receipts/outbox/checkpoints;
- settings, non-sensitive audit metadata, and attachment metadata.

The database must not contain:

- Apple Account credentials;
- iMessage database copies or extracted Apple tokens;
- TLS private keys, device identity private keys, pairing secrets, or database keys;
- raw QR payloads;
- durable ANCS notification UIDs or content copied across sessions by default;
- plaintext diagnostic logs containing contact/message/notification data.

## Encryption and filesystem policy

1. On first run, generate a random 256-bit database key using the OS cryptographic RNG.
2. Protect the key for the current Windows user with `DataProtectionProvider("LOCAL=user")`, storing only the encrypted blob under the app's local data directory. Never put plaintext key material in app settings, environment variables, command lines, logs, crash metadata, or the database directory.
3. Open the SQLite3 Multiple Ciphers connection and apply the key before any schema access. The current default is sqleet-compatible ChaCha20-Poly1305 with authentication.
4. Enable encrypted WAL support, foreign keys, secure deletion, a busy timeout, and integrity checks.
5. Keep the database and attachments under the packaged app's local data directory with current-user-only ACLs.
6. Encrypt attachment contents independently with per-file AES-256-GCM keys wrapped by a master attachment key in protected credential storage. A random storage name—not the sender filename—is used on disk.
7. The optional Windows Hello inactivity lock covers private UI state and clears this app's ANCS notifications. Local-user DPAPI remains the at-rest key boundary; the current UI lock does not claim to re-wrap or withhold database keys.
8. Revocation and account reset perform cryptographic erasure by deleting protected keys before deleting cache files.

An encrypted provider is a requirement. The selected open-source provider is `SQLite3MC.PCLRaw.bundle`, rather than the deprecated unofficial SQLCipher bundle. If the native encrypted provider cannot initialize, or if the protected key is incorrect, the app fails closed; it must not silently create a plaintext replacement database.

## Connection settings

After successful keying and before normal queries:

```sql
PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;
PRAGMA synchronous = FULL;
PRAGMA secure_delete = ON;
PRAGMA trusted_schema = OFF;
PRAGMA busy_timeout = 5000;
```

Startup performs `PRAGMA quick_check` on normal launches and a full integrity check after an unclean shutdown, migration failure, or explicit diagnostics request.

## Schema

The initial migration is shown as one unit for review. Phase 3 will embed it as a checksum-verified migration resource and execute it transactionally.

```sql
CREATE TABLE schema_migrations (
    version                 INTEGER PRIMARY KEY,
    name                    TEXT NOT NULL,
    checksum_sha256         BLOB NOT NULL CHECK(length(checksum_sha256) = 32),
    applied_at_utc          TEXT NOT NULL
) STRICT;

CREATE TABLE devices (
    device_id               TEXT PRIMARY KEY,
    display_name            TEXT NOT NULL CHECK(length(display_name) BETWEEN 1 AND 200),
    platform                TEXT NOT NULL CHECK(platform IN ('windows', 'ios')),
    trust_state             TEXT NOT NULL CHECK(trust_state IN ('trusted', 'revoked')),
    identity_public_key     BLOB NOT NULL CHECK(length(identity_public_key) = 32),
    tls_spki_sha256         BLOB CHECK(tls_spki_sha256 IS NULL OR length(tls_spki_sha256) = 32),
    protocol_floor          INTEGER NOT NULL DEFAULT 1 CHECK(protocol_floor >= 1),
    protocol_ceiling        INTEGER NOT NULL DEFAULT 1 CHECK(protocol_ceiling >= protocol_floor),
    paired_at_utc           TEXT NOT NULL,
    last_seen_at_utc        TEXT,
    revoked_at_utc          TEXT,
    capabilities_json       TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(capabilities_json)),
    permissions_json        TEXT NOT NULL DEFAULT '{}' CHECK(json_valid(permissions_json)),
    created_at_utc          TEXT NOT NULL,
    updated_at_utc          TEXT NOT NULL,
    CHECK((trust_state = 'revoked') = (revoked_at_utc IS NOT NULL))
) STRICT;

CREATE UNIQUE INDEX ux_devices_identity_key
    ON devices(identity_public_key);

CREATE TABLE device_endpoints (
    endpoint_id             TEXT PRIMARY KEY,
    device_id               TEXT NOT NULL REFERENCES devices(device_id) ON DELETE CASCADE,
    transport               TEXT NOT NULL CHECK(transport IN ('wifi', 'bluetooth-le')),
    host                    TEXT,
    port                    INTEGER CHECK(port IS NULL OR port BETWEEN 1 AND 65535),
    service_name            TEXT,
    last_succeeded_at_utc   TEXT,
    expires_at_utc          TEXT,
    created_at_utc          TEXT NOT NULL,
    updated_at_utc          TEXT NOT NULL,
    CHECK(
        (transport = 'wifi' AND host IS NOT NULL AND port IS NOT NULL)
        OR transport = 'bluetooth-le'
    )
) STRICT;

CREATE INDEX ix_device_endpoints_device
    ON device_endpoints(device_id, transport, last_succeeded_at_utc DESC);

CREATE TABLE contacts (
    contact_id              TEXT PRIMARY KEY,
    owner_device_id         TEXT NOT NULL REFERENCES devices(device_id) ON DELETE CASCADE,
    source_contact_id       TEXT NOT NULL,
    source_kind             TEXT NOT NULL CHECK(source_kind IN ('companion-contacts', 'bluetooth-pbap')),
    source_revision         INTEGER NOT NULL CHECK(source_revision >= 0),
    display_name            TEXT NOT NULL CHECK(length(display_name) <= 500),
    given_name              TEXT NOT NULL DEFAULT '' CHECK(length(given_name) <= 200),
    family_name             TEXT NOT NULL DEFAULT '' CHECK(length(family_name) <= 200),
    organization_name       TEXT NOT NULL DEFAULT '' CHECK(length(organization_name) <= 300),
    avatar_attachment_id    TEXT,
    is_deleted              INTEGER NOT NULL DEFAULT 0 CHECK(is_deleted IN (0, 1)),
    source_modified_at_utc  TEXT,
    created_at_utc          TEXT NOT NULL,
    updated_at_utc          TEXT NOT NULL,
    UNIQUE(owner_device_id, source_kind, source_contact_id)
) STRICT;

CREATE INDEX ix_contacts_owner_display
    ON contacts(owner_device_id, is_deleted, display_name COLLATE NOCASE);

CREATE TABLE contact_methods (
    contact_method_id       TEXT PRIMARY KEY,
    contact_id              TEXT NOT NULL REFERENCES contacts(contact_id) ON DELETE CASCADE,
    method_kind             TEXT NOT NULL CHECK(method_kind IN ('phone', 'email')),
    label                   TEXT NOT NULL DEFAULT '' CHECK(length(label) <= 100),
    display_value           TEXT NOT NULL CHECK(length(display_value) BETWEEN 1 AND 500),
    normalized_value        TEXT NOT NULL CHECK(length(normalized_value) BETWEEN 1 AND 500),
    sort_order              INTEGER NOT NULL DEFAULT 0 CHECK(sort_order >= 0),
    is_primary              INTEGER NOT NULL DEFAULT 0 CHECK(is_primary IN (0, 1)),
    UNIQUE(contact_id, method_kind, normalized_value, label)
) STRICT;

CREATE INDEX ix_contact_methods_normalized
    ON contact_methods(method_kind, normalized_value);

CREATE TABLE conversations (
    conversation_id         TEXT PRIMARY KEY,
    owner_device_id         TEXT NOT NULL REFERENCES devices(device_id) ON DELETE CASCADE,
    source_kind             TEXT NOT NULL CHECK(source_kind IN ('app-chat', 'eu-carrier')),
    source_conversation_id  TEXT,
    title                   TEXT NOT NULL DEFAULT '' CHECK(length(title) <= 500),
    created_at_utc          TEXT NOT NULL,
    updated_at_utc          TEXT NOT NULL,
    last_message_at_utc     TEXT,
    is_archived             INTEGER NOT NULL DEFAULT 0 CHECK(is_archived IN (0, 1)),
    is_deleted              INTEGER NOT NULL DEFAULT 0 CHECK(is_deleted IN (0, 1)),
    UNIQUE(owner_device_id, source_kind, source_conversation_id)
) STRICT;

CREATE INDEX ix_conversations_recent
    ON conversations(is_deleted, is_archived, last_message_at_utc DESC);

CREATE TABLE conversation_participants (
    conversation_id         TEXT NOT NULL REFERENCES conversations(conversation_id) ON DELETE CASCADE,
    participant_id          TEXT NOT NULL,
    contact_id              TEXT REFERENCES contacts(contact_id) ON DELETE SET NULL,
    normalized_handle       TEXT NOT NULL DEFAULT '',
    display_name            TEXT NOT NULL DEFAULT '' CHECK(length(display_name) <= 500),
    role                    TEXT NOT NULL DEFAULT 'member' CHECK(role IN ('self', 'member')),
    PRIMARY KEY(conversation_id, participant_id)
) WITHOUT ROWID, STRICT;

CREATE TABLE messages (
    message_id              TEXT PRIMARY KEY,
    conversation_id         TEXT NOT NULL REFERENCES conversations(conversation_id) ON DELETE CASCADE,
    origin_device_id        TEXT NOT NULL REFERENCES devices(device_id) ON DELETE RESTRICT,
    origin_sequence         INTEGER NOT NULL CHECK(origin_sequence > 0),
    source_message_id       TEXT,
    source_kind             TEXT NOT NULL CHECK(source_kind IN ('app-chat', 'eu-carrier')),
    direction               TEXT NOT NULL CHECK(direction IN ('incoming', 'outgoing', 'system')),
    sender_participant_id   TEXT,
    sender_handle           TEXT NOT NULL DEFAULT '',
    body                    TEXT NOT NULL DEFAULT '' CHECK(length(CAST(body AS BLOB)) <= 262144),
    content_type            TEXT NOT NULL DEFAULT 'text/plain',
    sent_at_utc             TEXT,
    received_at_utc         TEXT,
    delivery_status         TEXT NOT NULL CHECK(delivery_status IN (
                                'pending', 'sent', 'delivered', 'failed', 'received', 'unknown')),
    reply_to_message_id     TEXT REFERENCES messages(message_id) ON DELETE SET NULL,
    edit_revision           INTEGER NOT NULL DEFAULT 0 CHECK(edit_revision >= 0),
    is_deleted              INTEGER NOT NULL DEFAULT 0 CHECK(is_deleted IN (0, 1)),
    created_at_utc          TEXT NOT NULL,
    updated_at_utc          TEXT NOT NULL,
    UNIQUE(origin_device_id, origin_sequence),
    UNIQUE(source_kind, origin_device_id, source_message_id)
) STRICT;

CREATE INDEX ix_messages_conversation_time
    ON messages(conversation_id, is_deleted, COALESCE(sent_at_utc, received_at_utc, created_at_utc));

CREATE TABLE attachments (
    attachment_id           TEXT PRIMARY KEY,
    owner_device_id         TEXT NOT NULL REFERENCES devices(device_id) ON DELETE CASCADE,
    message_id              TEXT REFERENCES messages(message_id) ON DELETE CASCADE,
    storage_name            TEXT NOT NULL UNIQUE CHECK(length(storage_name) BETWEEN 1 AND 100),
    display_name            TEXT NOT NULL CHECK(length(display_name) BETWEEN 1 AND 500),
    media_type              TEXT NOT NULL CHECK(length(media_type) BETWEEN 1 AND 200),
    byte_count              INTEGER NOT NULL CHECK(byte_count BETWEEN 0 AND 26214400),
    content_sha256          BLOB NOT NULL CHECK(length(content_sha256) = 32),
    wrapped_file_key        BLOB NOT NULL,
    encryption_nonce        BLOB NOT NULL CHECK(length(encryption_nonce) = 12),
    transfer_state          TEXT NOT NULL CHECK(transfer_state IN (
                                'offered', 'transferring', 'verified', 'failed', 'deleted')),
    created_at_utc          TEXT NOT NULL,
    verified_at_utc         TEXT,
    deleted_at_utc          TEXT
) STRICT;

CREATE INDEX ix_attachments_message
    ON attachments(message_id);

CREATE TABLE app_notifications (
    notification_id         TEXT PRIMARY KEY,
    owner_device_id         TEXT NOT NULL REFERENCES devices(device_id) ON DELETE CASCADE,
    source_kind             TEXT NOT NULL CHECK(source_kind IN ('product', 'companion-own-app')),
    source_app_identifier   TEXT NOT NULL DEFAULT '',
    category                TEXT NOT NULL DEFAULT '',
    title                   TEXT NOT NULL DEFAULT '' CHECK(length(CAST(title AS BLOB)) <= 65536),
    body                    TEXT NOT NULL DEFAULT '' CHECK(length(CAST(body AS BLOB)) <= 262144),
    occurred_at_utc         TEXT NOT NULL,
    read_at_utc             TEXT,
    dismissed_at_utc        TEXT,
    expires_at_utc          TEXT,
    created_at_utc          TEXT NOT NULL
) STRICT;

CREATE INDEX ix_app_notifications_recent
    ON app_notifications(dismissed_at_utc, occurred_at_utc DESC);

CREATE TABLE call_history (
    call_id                 TEXT PRIMARY KEY,
    owner_device_id         TEXT NOT NULL REFERENCES devices(device_id) ON DELETE CASCADE,
    source_kind             TEXT NOT NULL CHECK(source_kind IN ('app-voip', 'bluetooth-pbap', 'eu-default-dialer')),
    source_call_id          TEXT,
    direction               TEXT NOT NULL CHECK(direction IN ('incoming', 'outgoing')),
    normalized_handle       TEXT NOT NULL DEFAULT '',
    display_handle          TEXT NOT NULL DEFAULT '',
    contact_id              TEXT REFERENCES contacts(contact_id) ON DELETE SET NULL,
    started_at_utc          TEXT NOT NULL,
    connected_at_utc        TEXT,
    ended_at_utc            TEXT,
    status                  TEXT NOT NULL CHECK(status IN (
                                'missed', 'declined', 'completed', 'failed', 'cancelled', 'unknown')),
    created_at_utc          TEXT NOT NULL,
    updated_at_utc          TEXT NOT NULL,
    UNIQUE(owner_device_id, source_kind, source_call_id)
) STRICT;

CREATE INDEX ix_call_history_recent
    ON call_history(started_at_utc DESC);

CREATE TABLE sync_events (
    event_id                TEXT PRIMARY KEY,
    origin_device_id        TEXT NOT NULL REFERENCES devices(device_id) ON DELETE RESTRICT,
    origin_sequence         INTEGER NOT NULL CHECK(origin_sequence > 0),
    event_type              TEXT NOT NULL CHECK(length(event_type) BETWEEN 1 AND 200),
    entity_id               TEXT NOT NULL DEFAULT '',
    occurred_at_utc         TEXT NOT NULL,
    received_at_utc         TEXT NOT NULL,
    payload_json            TEXT NOT NULL CHECK(json_valid(payload_json)),
    payload_sha256          BLOB NOT NULL CHECK(length(payload_sha256) = 32),
    apply_status            TEXT NOT NULL CHECK(apply_status IN ('pending', 'applied', 'rejected')),
    is_optional             INTEGER NOT NULL DEFAULT 0 CHECK(is_optional IN (0, 1)),
    last_error_code         TEXT,
    applied_at_utc          TEXT,
    prune_after_utc         TEXT,
    UNIQUE(origin_device_id, origin_sequence)
) STRICT;

CREATE INDEX ix_sync_events_origin_sequence
    ON sync_events(origin_device_id, origin_sequence);

CREATE INDEX ix_sync_events_prune
    ON sync_events(prune_after_utc)
    WHERE prune_after_utc IS NOT NULL;

CREATE TABLE sync_checkpoints (
    peer_device_id          TEXT NOT NULL REFERENCES devices(device_id) ON DELETE CASCADE,
    origin_device_id        TEXT NOT NULL REFERENCES devices(device_id) ON DELETE CASCADE,
    highest_contiguous_sequence INTEGER NOT NULL DEFAULT 0 CHECK(highest_contiguous_sequence >= 0),
    updated_at_utc          TEXT NOT NULL,
    PRIMARY KEY(peer_device_id, origin_device_id)
) WITHOUT ROWID, STRICT;

CREATE TABLE outbox_deliveries (
    event_id                TEXT NOT NULL REFERENCES sync_events(event_id) ON DELETE CASCADE,
    target_device_id        TEXT NOT NULL REFERENCES devices(device_id) ON DELETE CASCADE,
    delivery_state          TEXT NOT NULL CHECK(delivery_state IN ('pending', 'in-flight', 'acknowledged', 'failed')),
    attempt_count           INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count >= 0),
    next_attempt_at_utc     TEXT,
    last_attempt_at_utc     TEXT,
    acknowledged_at_utc    TEXT,
    last_error_code         TEXT,
    PRIMARY KEY(event_id, target_device_id)
) WITHOUT ROWID, STRICT;

CREATE INDEX ix_outbox_pending
    ON outbox_deliveries(target_device_id, delivery_state, next_attempt_at_utc);

CREATE TABLE processed_commands (
    command_id              TEXT PRIMARY KEY,
    origin_device_id        TEXT NOT NULL REFERENCES devices(device_id) ON DELETE CASCADE,
    command_type            TEXT NOT NULL CHECK(length(command_type) BETWEEN 1 AND 200),
    request_sha256          BLOB NOT NULL CHECK(length(request_sha256) = 32),
    command_state           TEXT NOT NULL CHECK(command_state IN ('accepted', 'running', 'succeeded', 'failed')),
    result_json             TEXT CHECK(result_json IS NULL OR json_valid(result_json)),
    error_code              TEXT,
    created_at_utc          TEXT NOT NULL,
    completed_at_utc        TEXT,
    expires_at_utc          TEXT NOT NULL
) STRICT;

CREATE INDEX ix_processed_commands_expiry
    ON processed_commands(expires_at_utc);

CREATE TABLE settings (
    scope                   TEXT NOT NULL CHECK(scope IN ('app', 'device')),
    scope_id                TEXT NOT NULL DEFAULT '',
    setting_key             TEXT NOT NULL CHECK(length(setting_key) BETWEEN 1 AND 200),
    value_json              TEXT NOT NULL CHECK(json_valid(value_json)),
    updated_at_utc          TEXT NOT NULL,
    PRIMARY KEY(scope, scope_id, setting_key),
    CHECK((scope = 'app' AND scope_id = '') OR (scope = 'device' AND scope_id <> ''))
) WITHOUT ROWID, STRICT;

CREATE TABLE retention_policies (
    data_class              TEXT PRIMARY KEY CHECK(data_class IN (
                                'contacts', 'app-messages', 'attachments',
                                'notifications', 'call-history', 'sync-receipts')),
    retention_days          INTEGER CHECK(retention_days IS NULL OR retention_days BETWEEN 0 AND 3650),
    retain_until_revoked    INTEGER NOT NULL DEFAULT 0 CHECK(retain_until_revoked IN (0, 1)),
    updated_at_utc          TEXT NOT NULL
) STRICT;

CREATE TABLE audit_log (
    audit_id                INTEGER PRIMARY KEY AUTOINCREMENT,
    occurred_at_utc         TEXT NOT NULL,
    event_code              TEXT NOT NULL CHECK(length(event_code) BETWEEN 1 AND 200),
    correlation_id          TEXT,
    redacted_subject_id     TEXT,
    outcome                 TEXT NOT NULL CHECK(outcome IN ('success', 'failure', 'cancelled')),
    error_code              TEXT,
    metadata_json           TEXT NOT NULL DEFAULT '{}' CHECK(json_valid(metadata_json))
) STRICT;

CREATE INDEX ix_audit_log_time
    ON audit_log(occurred_at_utc DESC);
```

The migration adds the `contacts.avatar_attachment_id` foreign key only after `attachments` exists, or orders the final implementation so the reference can be declared without a forward-reference issue. The review schema shows the semantic relationship; the executable migration will be validated by an empty-database integration test.

## Search indexes

Search is local and source-aware:

- Contacts: `display_name`, organization, phone display/normalized values, and email values.
- Conversations: title and participant names/handles.
- Messages: body only for sources the app is authorized to retain.
- Notifications: product/companion-owned retained records only.

The initial implementation starts with indexed normalized columns and `LIKE ... ESCAPE` queries for correctness. FTS5 is added only after the selected encrypted native build proves FTS5 support on x64 and ARM64. The app never creates a separate plaintext search index.

## Transaction rules

- An inbound sync event receipt, its projection update, and checkpoint advance occur in one transaction.
- An outbound domain change and its `sync_events`/`outbox_deliveries` rows occur in one transaction.
- An attachment is not linked as `verified` until file hash verification and database update can be made recoverable as one logical operation.
- Repository methods never expose a connection outside the persistence assembly.
- All write transactions are short and use cancellation/timeouts.
- `updated_at_utc` is audit/display metadata; conflict resolution uses origin sequence and source revision.

## Deduplication and conflicts

- `sync_events.event_id` and `(origin_device_id, origin_sequence)` prevent duplicate events.
- Duplicate IDs with a different payload hash are treated as protocol violations.
- Contact upserts apply only when `source_revision` is newer than the current row.
- Message IDs are immutable; edits create higher `edit_revision` status/upsert events.
- Tombstones remain until all trusted, non-revoked peers acknowledge the delete or retention expires.
- Device revocation prevents future delivery immediately; it does not silently delete user-retained data without the user's chosen retention action.

## Retention defaults

| Data class | Default |
|---|---|
| Authorized contacts | Until permission revoked/device removed, with user choice to retain or erase |
| App-owned messages | Until user deletes |
| Attachments | With owning message; incomplete transfers after 24 hours |
| Product/companion notifications | 30 days |
| PBAP/EU/app VoIP call history | 90 days |
| Applied sync payload receipts | 7 days after all targets acknowledge; immutable ID/hash receipt may remain longer |
| Processed command results | 24 hours minimum, then prune |
| Audit log | 30 days; metadata only |
| ANCS content/UIDs | Memory-only, current ANCS session |

Every retention setting is visible and supports immediate deletion. Security audit rows record the deletion operation but not deleted content.

## Backup and export

- Automatic cloud backup is off; the product is local-first.
- A user-requested diagnostic export contains schema version, app/OS versions, capability states, redacted error codes, and timing—no private records or secrets.
- A future encrypted data export requires a separate threat review and user-supplied passphrase; it is not part of v1.
- Copying the live database file is unsupported because WAL consistency and protected keys make it neither safe nor portable.

## Migration verification

Each migration test must:

1. create a fresh encrypted database;
2. apply migrations and verify checksums;
3. run foreign-key and integrity checks;
4. exercise every uniqueness/check constraint with valid and invalid rows;
5. close/reopen with the correct key;
6. prove that a wrong or absent key cannot read the schema;
7. upgrade from the previous schema fixture without losing permitted data;
8. verify rollback leaves the previous schema usable after an injected failure.
