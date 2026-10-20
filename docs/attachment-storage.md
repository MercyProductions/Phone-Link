# Attachment storage checkpoint: PLA2

The current receiver still accepts only selected images up to 25 MiB. Generic files/videos, bidirectional transfer, durable resume, duplicate-content detection, thumbnails, and a transfer queue remain planned work. This storage change is a prerequisite, not a claim those features work.

New incoming chunks are encrypted immediately; no plaintext `.incoming` file or whole-file encryption allocation is created. A random 256-bit file key is protected through `ISecretStore` before accepting the upload. At most four transfers overall and two per authenticated iPhone may be active. Each transfer is bound to its authenticated device and session. Session replacement/disconnect prunes incomplete staging and its protected key. A wrong-device completion cannot remove another device's transfer.

## On-disk format

All integers below are big-endian.

| Field | Bytes |
|---|---:|
| Magic `PLA2` | 4 |
| Plaintext chunk size (16–256 KiB) | 4 |
| Total plaintext length | 8 |
| Nonce base (8 random bytes, 4 zero bytes) | 12 |
| Per-chunk ciphertext, then 16-byte GCM tag | chunk plaintext length + 16 |

The final chunk's length derives from the authenticated total and chunk size. Each file has a fresh key. Per-chunk nonce is the first eight nonce-base bytes plus the unsigned 32-bit chunk index. Associated data is the full 28-byte header, expected whole-file SHA-256, 32-bit chunk index, and 32-bit plaintext chunk length. Header, order, length, and content are authenticated. A failed chunk/write aborts the transfer instead of allowing nonce reuse. A fresh, random protected-key reference also prevents duplicate attachment IDs from overwriting an existing file's key.

After the received byte count and incremental SHA-256 match the offer, the encrypted staging file is renamed and cataloged. New export uses bounded chunk buffers and performs a complete authentication/hash pass to `Stream.Null` before any plaintext reaches the caller, followed by a bounded-memory copy pass on the same read-only file handle. Trailing bytes and truncated files are rejected. The Windows Save a Copy caller additionally uses a transacted destination and commits only after successful export. No file is opened or executed automatically.

Previously cataloged `PLA1` imports remain readable through their original AES-GCM layout. That compatibility path retains the original 25 MiB bound and verifies the actual encrypted file length before allocation. It is not used for new uploads.

Crash-interrupted staging recovery/key garbage collection and persistent resume manifests are not implemented. Normal disconnect, failed chunk, incomplete completion, and application disposal remove incomplete staging. Files left by a process crash remain encrypted; the receiver currently does not resume them. Disk capacity and byte-count limits remain enforced.

Tests cover ordered chunk round-trip, bounded export writes, header/ciphertext/tag corruption, truncation, appended data, chunk reordering, zero plaintext output on corruption, wrong-device completion, duplicate completed IDs, concurrent-offer limits, disconnect cleanup, failed chunk cleanup, and PLA1 compatibility. Physical-iPhone delivery and crash recovery are separate gates.

Cryptographic constraint: never repeat a nonce with the same AES-GCM key. See the [.NET 10 AesGcm contract](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm.encrypt?view=net-10.0). PLA2 is an internal application format, not a new wire protocol or an independently audited cryptographic standard.
