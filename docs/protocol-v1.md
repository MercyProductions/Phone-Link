# Phone Link local protocol v1

Status: **frozen for initial implementation**  
Transport: local WebSocket over pinned TLS 1.3  
Encoding: UTF-8 JSON for control/events; binary WebSocket messages for attachment chunks

## Protocol goals

- Authenticate both paired devices without an Apple Account, Microsoft account, or cloud relay.
- Work offline on a trusted local network.
- Survive disconnects, duplicate delivery, process termination, and clock skew.
- Negotiate optional capabilities rather than guessing from OS versions.
- Reject oversized, malformed, replayed, stale, or unauthorized input before it reaches feature code.
- Preserve forward compatibility through versioned envelopes and payload types.

## Cryptographic primitives

| Purpose | Primitive |
|---|---|
| Transport confidentiality/integrity | TLS 1.3 using an ECDSA P-256 server certificate |
| TLS trust bootstrap | SHA-256 pin of the Windows server certificate SubjectPublicKeyInfo (SPKI) carried in the QR offer |
| Device identity | Ed25519 signing key pair per installation |
| Pairing offer proof | HMAC-SHA-256 with a random, single-use 256-bit pairing secret |
| Hashing | SHA-256 |
| Random values | OS cryptographic random generator |
| Secret storage | iOS Keychain; Windows protected credential storage/certificate store |

No custom cipher, password-derived key, hard-coded key, or unauthenticated encryption is permitted. TLS certificate validation fails closed on a pin mismatch.

## Identifiers and representation

- UUIDs are canonical lowercase strings with hyphens.
- Timestamps are UTC RFC 3339 with fractional seconds, for example `2026-08-12T19:04:31.427Z`.
- Byte strings in JSON use unpadded Base64URL.
- Phone numbers use an E.164 normalized value when possible and preserve the original display value separately.
- An origin sequence is a positive signed 64-bit integer, monotonically increasing per origin device and event log.
- JSON property names use `camelCase`.
- Receivers reject duplicate JSON properties and unknown enum values. Unknown payload fields are ignored only when the enclosing payload version is supported.

## Pairing offer

Windows creates an offer only after an explicit user action. The QR contains:

```text
phonelink://pair?data=<base64url(utf8-json)>
```

Decoded version 1 data:

```json
{
  "version": 1,
  "sessionId": "4abbe312-e81e-45ec-8c8e-6a474161ad7f",
  "expiresAt": "2026-08-12T19:06:31.427Z",
  "endpoints": [
    { "host": "192.168.1.24", "port": 45831 }
  ],
  "tlsSpkiSha256": "ujh8dvyHmZ-rWb89R_9JJDWssDHDqKCv5d7EfTuhY9Q",
  "serverIdentityKey": "87zSTqhtr8XBcE48lSfCL8LOqltcqfkEV1Q6JQhI8tA",
  "offerNonce": "ol5me50pmUBZ6eaItRaPYQ",
  "pairingSecret": "nEhkyZQgupMkDmLeEKD7tcSeyFWrX5nkakEvH-OI9Xw"
}
```

Rules:

- The offer expires after 120 seconds.
- `sessionId`, `offerNonce`, and `pairingSecret` are cryptographically random and single-use.
- The full QR payload is sensitive and must never be logged, persisted after completion/expiry, copied to the clipboard automatically, or included in diagnostics.
- Endpoint hosts must be local/private addresses or a local DNS name. The companion rejects public Internet endpoints in v1.
- The companion connects only if the TLS SPKI pin matches the QR.

## Pairing handshake

1. iPhone validates scheme, version, expiry, fields, and endpoint scope.
2. iPhone generates or loads its Ed25519 installation identity.
3. iPhone connects to `/pair/v1` over TLS 1.3 and validates `tlsSpkiSha256`.
4. iPhone sends `pairing.clientHello` with a fresh device ID, public identity key, app version, phone display name, and a HMAC proof.
5. Windows atomically consumes the pairing session. A second use is rejected even if the first flow later fails.
6. Windows responds with `pairing.serverProof`, signed by the `serverIdentityKey` carried in the QR.
7. Both devices derive the same six-word comparison phrase from the SHA-256 pairing transcript and show it to the user.
8. The user confirms on both devices. Either rejection cancels pairing.
9. Each side persists the peer's device ID, identity public key, TLS SPKI pin, creation time, and granted capabilities. Private keys remain in protected platform storage.
10. Both sides erase offer material from memory as soon as practical.

The client proof input is the SHA-256 digest of the following length-prefixed byte strings in order:

```text
"PhoneLink-Pairing-Client-v1"
rawOfferJsonBytes
clientDeviceIdUtf8
clientIdentityPublicKeyBytes
clientNonceBytes
```

`proof = HMAC-SHA-256(pairingSecret, digest)`.

The server signature input is the SHA-256 digest of:

```text
"PhoneLink-Pairing-Server-v1"
rawOfferJsonBytes
clientDeviceIdUtf8
clientIdentityPublicKeyBytes
clientNonceBytes
serverNonceBytes
```

The comparison phrase is derived from the first 66 bits of:

```text
SHA-256("PhoneLink-Pairing-Compare-v1" || serverSignatureInput || serverSignature)
```

mapped to six entries in a fixed 2,048-word list included identically in both clients. This phrase is a human confirmation signal, not a key.

## Authenticated session handshake

After pairing, the iPhone connects to `/sync/v1`:

1. TLS 1.3 completes and the companion checks the stored Windows SPKI pin.
2. Client sends `session.clientHello` with device ID, 256-bit nonce, supported protocol range, app version, and last local connection epoch.
3. Server finds a non-revoked trust record and replies with `session.serverChallenge`: chosen protocol, server nonce, unique session ID, current server time, and an Ed25519 signature over the transcript.
4. Client verifies the stored Windows identity key and replies with `session.clientProof`, signing the same transcript plus the chosen version.
5. Server verifies the stored iPhone identity key and replies `session.ready` with current capabilities and retention limits.
6. Both sides bind all following messages to the WebSocket session ID. A proof from another connection cannot be replayed because both fresh nonces and the server-generated session ID are signed.

Authentication fails without retry when the TLS pin or stored identity key mismatches. The UI instructs the user to inspect and re-pair; it never silently trusts a replacement key.

## JSON envelope

Every control/event text message uses this outer object:

```json
{
  "protocolVersion": 1,
  "messageId": "e67ab025-ddcf-4394-a4f4-123e1c01ddaa",
  "correlationId": null,
  "sessionId": "dfb0aacc-e837-4aaa-8387-70fe46eaa138",
  "originDeviceId": "ca8cf3a6-e6ab-4ac7-be7f-6b5736a14ec3",
  "originSequence": 1042,
  "sentAt": "2026-08-12T19:07:12.109Z",
  "kind": "sync.batch",
  "requiresAck": true,
  "payload": {}
}
```

Handshake messages before `session.ready` use `originSequence: 0` and are not inserted into the durable event log. All durable event batches use positive origin sequences.

## Message kinds

| Kind | Direction | Durable | Purpose |
|---|---|---:|---|
| `pairing.clientHello` | iPhone → Windows | No | Prove possession of QR offer |
| `pairing.serverProof` | Windows → iPhone | No | Authenticate Windows pairing endpoint |
| `pairing.confirm` | Either | No | Record explicit human approval/rejection |
| `pairing.complete` | Windows → iPhone | No | Return final trust metadata |
| `session.clientHello` | iPhone → Windows | No | Begin trusted session |
| `session.serverChallenge` | Windows → iPhone | No | Authenticate server identity/freshness |
| `session.clientProof` | iPhone → Windows | No | Authenticate paired iPhone identity |
| `session.ready` | Windows → iPhone | No | Finish negotiation |
| `sync.offer` | Either | No | Advertise current checkpoints and available ranges |
| `sync.request` | Either | No | Request a contiguous event range |
| `sync.batch` | Either | Yes | Deliver ordered durable events |
| `sync.ack` | Either | Yes | Advance contiguous accepted checkpoint |
| `sync.nack` | Either | No | Reject an event/range with a stable error |
| `command.request` | Either | Yes | Request an authorized side effect |
| `command.result` | Either | Yes | Return an idempotent command outcome |
| `attachment.offer` | Either | Yes | Offer attachment metadata |
| `attachment.accept` | Either | Yes | Accept and set chunk window |
| `attachment.complete` | Either | Yes | Confirm final verified file |
| `ping` / `pong` | Either | No | Liveness and latency |
| `session.close` | Either | No | Graceful close reason |

## Capability negotiation

`session.ready` includes a sorted list of capabilities and per-feature permission state:

```json
{
  "capabilities": [
    "contacts.sync",
    "photos.import",
    "command.call.start.confirmed"
  ],
  "maximumBatchEvents": 200,
  "maximumAttachmentBytes": 26214400
}
```

A command/event requiring an unnegotiated capability is rejected with `protocol.capability.not-negotiated`. Availability is recalculated on each connection.

## Durable sync event

Events inside `sync.batch.payload.events` use:

```json
{
  "eventId": "e8e1ba29-8f82-46f1-ac4c-74fc627589d4",
  "originDeviceId": "ca8cf3a6-e6ab-4ac7-be7f-6b5736a14ec3",
  "originSequence": 1042,
  "occurredAt": "2026-08-12T19:07:11.814Z",
  "eventType": "contact.upsert",
  "entityId": "ios-contact-5D4C13E8",
  "payload": {}
}
```

Uniqueness constraints:

- `eventId` is globally unique.
- `(originDeviceId, originSequence)` is unique and contiguous within a device event log.
- Re-delivery of the same ID and identical SHA-256 canonical payload is a successful duplicate.
- Reuse of an ID or origin sequence with different content is a security/protocol violation and closes the session.

## Event type registry

The registry reserves names for staged modules. The current executable sync path accepts contact upserts/deletes and attachment transfer messages; app messaging and EU carrier event handlers are not advertised until implemented and authorized at runtime.

| Event type | Capability | Retention |
|---|---|---|
| `permission.changed.v1` | Core | Current state + audit timestamp |
| `contact.upsert.v1` | `core.contacts.read` | User-configured authorized replica |
| `contact.delete.v1` | `core.contacts.read` | Tombstone until all peers acknowledge |
| `device.status.v1` | `core.device-status` | Latest value only |
| `app-message.upsert.v1` | `core.app-messaging.text` | User retention policy |
| `app-message.status.v1` | `core.app-messaging.text` | With parent message |
| `notification.own-app.v1` | `core.notification-own-app` | User retention policy |
| `attachment.manifest.v1` | `core.attachments.transfer` | With owning entity |
| `call.app-voip-event.v1` | `core.app-voip` | User retention policy |
| `carrier-message.upsert.v1` | EU carrier capability | Only events legitimately received by configured default app |
| `carrier-call-history.upsert.v1` | EU default-dialer capability | Only history since the app became default |

ANCS UIDs are deliberately absent. ANCS is a Windows-side, session-scoped accessory feed and is not synchronized as a durable companion event.

## Contact payload

The companion sends only fields the user authorized and the product actually uses:

```json
{
  "sourceContactId": "5D4C13E8-0FD0-4834-A7E8-63A0A5A34A45",
  "givenName": "Taylor",
  "familyName": "Morgan",
  "organizationName": "",
  "displayName": "Taylor Morgan",
  "phoneNumbers": [
    {
      "label": "mobile",
      "displayValue": "+1 (312) 555-0198",
      "normalizedValue": "+13125550198"
    }
  ],
  "emailAddresses": []
}
```

Contact notes are not collected. Addresses, birthdays, relationships, and social profiles are excluded until a real feature requires them and the privacy review is updated.

## Synchronization algorithm

1. The iPhone writes a file-protected outbox snapshot before sending contact batches.
2. A retry reuses the original event IDs, origin sequences, and batch IDs.
3. Windows validates monotonically increasing events against the highest applied sequence in encrypted SQLite.
4. An exact event-ID/sequence/SHA-256 retry is acknowledged without reapplying the projection.
5. Reusing an ID or sequence with different content is rejected; a gap is rejected with the expected next sequence.
6. Windows applies the idempotent contact projection, records the immutable receipt, and returns `sync.ack` with the highest contiguous sequence.
7. iPhone advances its local identifier checkpoint and removes its protected outbox only after every batch is acknowledged.

The schema retains `sync.offer`, `sync.request`, and `sync.nack` for the next recovery extension. Protocol v1 currently implements the direct contiguous batch/ack path.

For a two-device trust relationship, this is equivalent to a compact per-origin vector checkpoint without requiring a conflict-prone global clock.

## Commands and idempotency

Commands perform side effects such as a confirmed dial request, app-owned message send, or optional MAP reply:

```json
{
  "commandId": "41dde30a-0ac0-4739-921f-44a49e169e37",
  "name": "call.start",
  "expiresAt": "2026-08-12T19:08:12.109Z",
  "arguments": {
    "phoneNumber": "+13125550198"
  },
  "requiresUserConfirmation": true
}
```

- The implemented `call.start` path sends one expiring request, does not automatically retransmit it, and requires a fresh foreground confirmation on iPhone before every `tel:` launch.
- The `processed_commands` schema reserves durable command replay protection for future side effects that can be retried. It is not advertised as an implemented capability yet.
- Expired commands return `command.expired`.
- A declined or failed call request returns a correlated `command.result`; Windows never interprets network delivery alone as a completed call.

## Attachment binary frame

WebSocket binary messages use this big-endian header:

| Bytes | Field |
|---:|---|
| 4 | ASCII magic `PLB1` |
| 1 | Frame version: `0x01` |
| 1 | Frame type: `0x01` attachment chunk |
| 2 | Reserved zero bytes |
| 16 | Attachment UUID bytes in RFC/network order |
| 4 | Zero-based chunk index (`UInt32`, big-endian) |
| 4 | Payload length (`UInt32`, big-endian) |
| 32 | SHA-256 of this payload |
| N | Payload bytes |

The manifest includes total size, final SHA-256, media type, original safe display name, and chunk count. File paths are never transmitted. The receiver chooses its own app-private path, strips control characters, and never uses a sender name as a path component.

## Limits

| Item | v1 limit |
|---|---:|
| JSON WebSocket message | 1 MiB |
| Encoded `sync.batch` payload | 512 KiB |
| Events per batch | 200 negotiated; Contacts currently sends at most 100 |
| Message/notification text field | 256 KiB UTF-8 |
| Contact methods per contact | 64 |
| Capabilities per peer | 128 |
| Attachment default maximum | 25 MiB |
| Attachment chunk | 128 KiB currently; 256 KiB protocol maximum |
| In-flight attachment chunks | 1 (ordered transfer) |
| Pairing offer lifetime | 120 seconds |
| Idle ping interval | 25 seconds |
| Pong timeout | 10 seconds |

Limits are enforced before allocation where possible. A negotiated limit may be lower, never higher than the local hard limit.

## Error payload

```json
{
  "code": "sync.sequence.gap",
  "recoverability": "retryable",
  "message": "A contiguous event range is required.",
  "details": {
    "expectedSequence": 1042
  }
}
```

`message` is safe for diagnostics but not necessarily final UI copy. Details use a whitelist per error code and never include secrets or private content.

Recoverability values:

- `retryable`
- `user-action-required`
- `update-required`
- `security-failure`
- `permanent`

## Versioning

- Envelope `protocolVersion` changes only for incompatible framing/handshake behavior.
- Payload types carry `.v1`, `.v2`, and so on.
- Peers negotiate the highest common protocol version.
- There is no downgrade below the highest mutually supported version after either peer has recorded a successful higher-version session, unless the user explicitly resets pairing.
- Unsupported mandatory payloads yield `protocol.payload.unsupported`; optional payloads may be skipped only when the batch marks them optional.

## Protocol test fixtures

The repository will keep language-neutral fixtures for:

- valid/expired/replayed pairing offers;
- pairing proof and comparison phrase derivation;
- session signature transcripts;
- envelope parsing and duplicate-property rejection;
- event deduplication and sequence gaps;
- capability negotiation;
- oversized frames and fields;
- attachment chunk/final hashes;
- known error serialization.

Both .NET and Swift test suites must produce identical fixture results before a protocol change is accepted.
