# USB forwarding and static offset inspection

Implemented November 2, 2026. These are advanced tools, not automatic phone repair or proof of authenticated SSH access. Open **USB & binary tools** in the Windows navigation.

## Managed USB forwarding

Connect exactly one owned USB device, choose an existing service port, and select **Start loopback forwarder**. The app chooses a local port on IPv4 `127.0.0.1` only and displays byte counts, active connections, and failure categories. **Stop** closes the listener and active streams; navigation away, Phone Link lock, and app exit also end its lifetime. Stop remains available during binary inspection.

The selected USB serial is normalized and hashed in memory. Every new channel re-lists transport devices and requires exactly one matching hash; an absent device is not replaced with a different phone. This avoids accidental target substitution, but it is not cryptographic hardware authentication. Raw serials and payloads are not included in tunnel statistics. An ephemeral transport ID may still become invalid during disconnect/reconnect, causing failure rather than transparent recovery.

The forwarder permits four simultaneous connections by default, uses bounded 64 KiB direction buffers cleared after use, imposes a ten-second connection deadline, and allows up to ten seconds to drain a response after one side ends. It does not scan ports, install a server, authenticate, access pairing credentials, or issue application commands. Loopback is not an authorization boundary against other local processes: any local process able to connect to the port can use the selected device service while forwarding is active. Forward only a service you intend to expose locally, and stop when finished. Forwarding adds no encryption; use the service's own authentication/encryption and normal SSH host-key verification.

```powershell
dotnet run --project tools/PhoneLink.DeviceProbe/PhoneLink.DeviceProbe.csproj -- --verify-tunnel
dotnet run --project tools/PhoneLink.DeviceProbe/PhoneLink.DeviceProbe.csproj -- --forward 22
```

`--verify-tunnel` uses a temporary forwarder to fixed information port 62078 and sends one allowlisted `GetValue(ProductVersion)` request. Its result reports success and byte statistics, not the version value. It closes all temporary resources. `--forward` stays active until Ctrl+C; it sends no login or command itself. A listening local port does not mean the device port is available. The separate `--ssh-check` remains a bounded standard-port identification check, not a host-key or login test.

Physical proof: CLI and Windows UI verification both returned success with 189 bytes sent and 329 received, zero active connections after cleanup, and no phone changes. Standard SSH port 22 previously returned transport rejection code 3; no authenticated SSH session has been established. Automated tests cover duplex transfer, client half-close, cancellation, listener release, concurrency limits, connector failure, safe statistics, and identifier normalization. Physical unplug/replug, lock, and multi-phone identity substitution still need dedicated hardware validation.

## Static Mach-O offset inspector

Choose a local unpacked little-endian 64-bit Mach-O image or supported universal FAT32/FAT64 container. The file is read, never executed, modified, uploaded, decrypted, or obtained from phone memory. The report records SHA-256, architecture/subtype, UUID, sections, segments, symbol records, function starts, and exports. The UI shows at most 500 entries per image; JSON retains all parsed entries up to explicit aggregate limits.

Addresses are hexadecimal strings. File offsets refer to the outer file, including a universal slice's base. Image-relative offsets, unslid virtual addresses, constants, and unavailable mappings remain distinct. Undefined and absolute symbols are not invented file addresses. `RuntimeAddressesAvailable` is always false: no ASLR slide, kernel address, process memory, or private API offset is guessed.

```powershell
dotnet run --project tools/PhoneLink.OffsetDumper/PhoneLink.OffsetDumper.csproj -- 'C:\path\owned-image.dylib'
```

The CLI prints JSON; the Windows page provides explicit **Save JSON report**. Reports can contain image UUIDs and proprietary symbol names; review before sharing. Unsupported/compressed/IPSW/archives, filesets, malformed ranges, unsupported byte order, and invalid symbol mappings fail clearly. Encryption is flagged, not removed. Stripped images can legitimately contain few or no names. Input size is bounded at 8 GiB; load commands, symbol/string tables, export traversal, and aggregate output have independent limits. Truncation is explicit.

Verification: 20 parser tests exercise thin/universal fixtures, mapping, missing file backing, constants, encryption, malformed offsets/ranges/trie cycles, shared output limits, and cancellation. [Native compiler CI](https://github.com/MercyProductions/Phone-Link/actions/runs/41583883947) compiled this project's own ARM64/x86_64 libraries and a universal image, then compared symbol addresses, export addresses, UUID preservation, and file mappings with Apple's `nm`/`otool`. The universal artifact was also parsed on Windows. No private phone images or identifiers were uploaded.

The normal report remains a static-image inspection, not fulfillment of a runtime process/kernel dumper. The separate [companion-owned runtime image probe and optional CLI import](companion-runtime-image.md) add own-image metadata and explicit UUID/architecture/layout-gated text mapping through `--runtime <reviewed-json>`. Static semantics remain unchanged; the separate mapping is snapshot-derived, unauthenticated and not verified live. Other-process/kernel access, SSH endpoints/authentication, deeper diagnostics, repair validation, and accessory operations remain separate open work in the [active goal checkpoint](accessory-repair-goal.md).

Windows UI validation covered navigation, live USB verification, listener Start/Stop, and absence of the listener after Stop. Updated November 3: moved image import and report export to Windows App SDK desktop pickers bound to the app's WindowId. The legacy Save picker still rejected the helper's Save action as a different PickerHost process even after the documented refresh/retry; the desktop Save picker accepted it. Retained StorageFile transactional serialization/commit through a path-to-StorageFolder adapter, with explicit overwrite prompting and loaded/lock/cancellation checks. Existing destinations are opened without pre-truncation; a new empty file may remain if a post-selection operation fails before commit.

The real Windows UI selected this project's native ARM64 fixture, populated all three rows at `0x2e8`, showed the expected architecture/UUID/hash, and saved a new `artifacts/offset-ui-20261103-arm64.json` (no existing file overwritten). The complete exported JSON matched the native CI artifact after JSON normalization; source SHA-256 remained `fa62d120f96bb38f7fa492bfabe030cd984009bb03ca5ccf48c6f63b4b949757`, 16,800 bytes, UUID `c6f02115-8384-32d6-bc05-165e6637d950`, runtime addresses false. The file is a project-owned test library, not a phone-memory image. No source was executed, uploaded or changed. Architecture switching, cancellation/unload during I/O, existing-file replacement and real target-image compatibility remain runtime gates. Full solution build/test after migration: zero warnings/errors, 317 tests passed. The local development MSIX predates this milestone.
