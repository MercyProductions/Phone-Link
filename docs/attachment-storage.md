# Attachment storage checkpoint: PLA2

The receiver accepts selected images and MP4/MOV videos up to 25 MiB per item. The video picker is enabled only for an authenticated Windows session advertising `videos.import.v1`; older Windows builds remain photo-only. The iPhone reads the selected video through the system picker's file representation with a bounded read; it does not scan the library or create a Phone Link temp copy. Generic files, large videos, bidirectional transfer, durable resume, duplicate-content detection, thumbnails, and a transfer queue remain planned work.

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

In Windows **Photos**, use **Refresh imports** after a completed transfer and choose **Preview**. The preview uses the same verified decryption path in a native memory stream, not a temporary plaintext file. Image preview checks source dimensions and scales to at most 1,600 pixels on the longest edge. Video playback is not automatic: press Play. HEIC images and certain video/audio codecs may need Windows codec support. Closing the preview, locking Phone Link, or navigating away releases the preview resources and stops playback. Preview does not add iPhone library browsing. The transfer tests verify encrypted import, cataloging, export and tamper detection for both declared video types; they do not prove picker compatibility or playable codecs on a physical phone and PC.

Windows playback smoke check: the host's native MediaPlayer opened and played silent 32×32 H.264 samples to `MediaEnded` from `InMemoryRandomAccessStream` for both `video/mp4` and `video/quicktime`, with system media commands disabled. This verifies local codec/stream compatibility for those samples, not the WinUI render or an authenticated iPhone transfer.

Normal disconnect, failed chunk, incomplete completion, and application disposal remove incomplete staging. New transfers use a delete-on-close attachment-ID reservation and a distinct encrypted staging filename that identifies its scoped random protected key. The encryption format of completed files is unchanged. At startup, Phone Link considers at most 64 recognized new-format files under its own attachment directory. It removes only stages at least 24 hours old that have no corresponding completed file and can be opened without conflicting with a live writer. It deletes the matching staging key and file, then reports aggregate counts in **Troubleshooter**. It does not sweep old-format stages, reparse points, completed files, catalog records, or the iPhone. An orphaned final file from a crash between move and catalog commit is deliberately preserved. Incomplete stages are not resumed; disk capacity and byte-count limits remain enforced.

Tests cover ordered chunk round-trip, bounded export writes, header/ciphertext/tag corruption, truncation, appended data, chunk reordering, zero plaintext output on corruption, wrong-device completion, duplicate completed IDs, concurrent-offer limits, disconnect cleanup, failed chunk cleanup, and PLA1 compatibility. Recovery tests use real Windows files and an isolated in-memory key store to verify old-stage removal, live-writer and completed-file protection, and cross-manager reservation. A separate Windows smoke run created a synthetic transfer with an isolated real DPAPI store, exited without disposing the manager, and confirmed a second process removed the single aged stage and its protected key. Windows had already removed the delete-on-close reservation. No production attachment folder, saved key, or physical iPhone was used in this test.

Cryptographic constraint: never repeat a nonce with the same AES-GCM key. See the [.NET 10 AesGcm contract](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm.encrypt?view=net-10.0). PLA2 is an internal application format, not a new wire protocol or an independently audited cryptographic standard.
