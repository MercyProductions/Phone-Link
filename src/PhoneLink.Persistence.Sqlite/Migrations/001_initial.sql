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
