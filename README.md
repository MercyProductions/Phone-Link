# Phone Link

A local-first Windows + iPhone integration project, with supported platform integrations and separately labeled experimental, owner-authorized device-protocol research.

## Current status

The Windows app builds and launches on this Windows 11 workstation. A signed x64 development MSIX was built on November 3; `artifacts/windows/msix/latest.json` selects its versioned artifact set. It includes app-identity diagnostics but predates crash analysis, the audio receiver, companion health snapshots and Developer Mode status probing, and has not been trusted or installed. All 354 .NET tests and 10 packaging-artifact checks passed. The companion, including the new health checks, passed [native Xcode/iOS simulator CI on November 3](https://github.com/MercyProductions/Phone-Link/actions/runs/41598539264). Physical-iPhone companion installation and signed-device verification remain open.

Implemented:

- one-time QR pairing with TLS SPKI pinning, Ed25519 device identities, HMAC client proof, server proof, and six-word human comparison;
- mutually authenticated WSS sessions and Bonjour/DNS-SD reconnect discovery;
- a live PC network-interface inventory, bounded private IPv4/IPv6 pairing endpoints, and current trusted-device connection status;
- Windows DNS-SD discovery for Phone Link, AirPlay, and network-audio advertisements, displayed as unverified service observations;
- local-user DPAPI secret protection, encrypted SQLite, per-file AES-GCM photo encryption, and iOS Keychain identities;
- authorized Contacts sync with per-PC protected outboxes, authoritative recovery checkpoints, permission-subset reconciliation, and idempotent Windows receipts;
- user-selected Photos import with chunk/final hashes, encrypted staging, bounded-memory PLA2 storage/export, and legacy import compatibility;
- live Apple Notification Center Service capability detection and an in-memory ANCS GATT session when Windows exposes it;
- phone-labeled ANCS action commands with confirmation, stale-revision checks, serialized writes and no automatic retry; physical operation remains unverified because current characteristic access is denied;
- an iPhone media page with explicitly selected AMS sessions, advertised-command controls, timestamped metadata, confirmation and cancellation; current native setup also hits characteristic AccessDenied, so playback effects remain unverified;
- a separate [phone-to-PC Bluetooth audio receiver](docs/bluetooth-audio-receiver.md) with explicit selection/confirmation, Stop and lifecycle cleanup; native source discovery and Cancel verified, actual media sound not yet tested;
- native Windows app notifications for new, non-silent ANCS events, cleared when the session ends or the app locks;
- live MAP/PBAP capability checks without assuming that Windows exposes those profiles;
- user-confirmed click-to-call: Windows requests a number, then iPhone requires a tap before opening `tel:`;
- immediate trusted-device revocation and live-session termination;
- close-to-tray operation with an explicit Exit command and opt-in Windows Hello protection at startup, after five minutes without interaction, on tray hiding, and on Windows session lock/suspend;
- modern WinUI pages for Messages, Contacts, Photos, Calls, Notifications, Devices, Settings, and About;
- a live iPhone Troubleshooter for Apple USB presence, Windows problem flags, Apple Devices installation, local pairing paths, and authenticated companion sessions, with a minimal shareable report;
- local analysis of user-selected type-309 JSON crash reports, with allowlisted signatures and a separately copied redacted summary; real UI import/display/copy verified with the synthetic fixture, not yet with owner reports; see [scope and verification](docs/crash-report-analysis.md);
- explicit iPhone-side health capture and per-snapshot confirmed sharing to the connected PC, with a bounded/session-bound Windows inbox; storage stays on iPhone, no permission prompts or repairs run; see [verification and physical-device gates](docs/companion-health-snapshots.md);
- explicit experimental USB transport and read-only iPhone-information probes, without an iOS companion prerequisite for those probes;
- a separate explicit read-only Developer Mode status probe; current phone returns GetProhibited, so its state stays unknown; no setting or debug service is enabled;
- per-field phone-query outcomes and bounded automatic/manual recovery of a missing app discovery advertisement, with before/after checks;
- managed loopback-only USB forwarding with target rechecks, Stop controls, byte statistics, and a read-only round-trip verifier;
- bounded static Mach-O symbol/offset inspection with architecture/UUID/hash identity, a Windows table, JSON export, and portable CLI; native ARM64 fixture import/table/export verified end-to-end in Windows;
- a reproducible signed development MSIX build.

Important limits:

- Apple exposes no public API for a third-party companion to read the iMessage/SMS database, so the app does not claim iMessage history.
- MAP, PBAP, and ANCS work only when the paired iPhone and Windows Bluetooth stack expose those public accessory services.
- A third-party companion cannot silently control carrier calls. The implemented portable path requires foreground user confirmation on iPhone.
- ANCS identifiers and content are valid only for the current Bluetooth session and are never persisted.
- Automatic companion reconnect runs only in the foreground, observes network availability, and respects Disconnect. No unrestricted iOS background execution is claimed.
- Photo imports remain limited to 25 MiB each. Generic files, large videos, and durable resume are not implemented yet; see [attachment storage](docs/attachment-storage.md).
- Windows Hello must already be available for the optional app lock; Phone Link does not create a fallback PIN or replace Windows sign-in security.

See the detailed [feature feasibility matrix](docs/phase-1-feature-feasibility.md), [architecture](docs/phase-2-architecture.md), [protocol](docs/protocol-v1.md), [database schema](docs/database-schema.md), and [threat model](docs/security-threat-model.md).

The [iPhone toolbox expansion map](docs/iphone-toolbox.md) covers diagnostics, backup/migration, user-shared crash reports, hardware checks, official recovery, repair assistance, and the broader Windows integration tracks. USB information checks are verified on the attached phone; automatic phone repair is not. See [experimental USB diagnostics](docs/experimental-usb-diagnostics.md), [app-discovery recovery](docs/app-discovery-recovery.md), [ANCS actions](docs/ancs-actions.md), [AMS media controls](docs/ams-media-controls.md), [USB forwarding and static offsets](docs/usb-tunnels-and-static-offsets.md), and the [active goal checkpoint](docs/accessory-repair-goal.md). Both ANCS and AMS characteristic enumeration return AccessDenied after service access returns Allowed; no real notification action or playback command has been verified. The Troubleshooter now records the current process's Windows package identity to support a packaged-versus-standalone comparison, not to declare a cause. Local registration recovery is not proof of phone reconnection, forwarding is not authenticated SSH, and static inspection is not a runtime dump.

The [expanded-vision roadmap](docs/expanded-vision-roadmap.md) tracks the October requirements, current gaps, and verification evidence. Direct executable launches use `%LOCALAPPDATA%/MercyProductions/PhoneLink` for protected local data; installed packages retain their package-local storage. Installing a package does not migrate companion trust. Scoped link-local IPv6 addresses are displayed as interface metadata but excluded from QR offers because Windows interface scope IDs cannot be reused on iPhone.

## Build and test Windows

Requirements: Windows 11, .NET SDK 10, Visual Studio 2026 with the .NET desktop and Windows app development workloads, and Windows SDK 10.0.26100 or newer.

```powershell
dotnet build PhoneLink.slnx
dotnet test PhoneLink.slnx --no-build
dotnet list PhoneLink.slnx package --vulnerable --include-transitive
```

The self-contained Debug executable is under `src/PhoneLink.Windows/bin/Debug/net10.0-windows10.0.26100.0/win-x64/PhoneLink.Windows.exe` after a normal build.

## Build and install the local MSIX

```powershell
.\packaging\Build-LocalMsix.ps1
.\packaging\Test-PackageArtifacts.ps1
.\packaging\Install-LocalMsix.ps1 -WhatIf
# Only after reviewing and approving certificate trust and installation:
.\packaging\Install-LocalMsix.ps1
```

Each successful build publishes a separate `artifacts/windows/msix/builds/<BuildId>/` set containing the MSIX, public certificate, build log, and `build.json` with source revision/dirty status and SHA-256 hashes. Only a complete successful set replaces `latest.json`; earlier sets and legacy root artifacts are preserved. The installer selects `latest.json` by default, or an explicit `-BuildId <BuildId>`, and rejects mismatched hashes and package/certificate signers. These local hashes detect accidental changes, not independently authenticated provenance.

The installer prompts before trusting the local public certificate and before installing the package. `-WhatIf` performs selection and validation without either change. The build script uses isolated output, creates a temporary private key in the current-user certificate store, signs the MSIX, exports only the public `.cer`, and removes the private certificate. Building alone does not trust or install it. Trusting a development certificate trusts code signed by it; review its thumbprint first. Windows signature policy must validate before installation. This development certificate is for local sideload testing only; public distribution requires Microsoft Store signing or a production code-signing identity.

## Build the iPhone companion

### Pairing QR prerequisite

The Windows QR is a one-time `phonelink://pair?data=…` pairing offer, **not an iPhone app-download link**. It requires this repository's custom Phone Link companion, not Microsoft's app. The repository currently supplies an Xcode project, not an App Store/TestFlight installation link. Install the companion on your iPhone using the Mac/Xcode steps below first (current deployment target: iOS 26).

In the installed companion choose **Pair with Windows → Scan QR code**, scan a fresh Windows code, choose **Connect securely**, and compare/approve the six words on both devices. Keep both on the same local network. If Apple Camera reports “No usable data found,” use the in-companion scanner; installing the Windows package alone cannot enable iPhone pairing. Updated companion builds also register the `phonelink` URL scheme and route external links to the same validated review screen, without automatically connecting or trusting a PC. Apple Camera recognition on physical hardware still requires testing. See Apple's [custom URL scheme documentation](https://developer.apple.com/documentation/xcode/defining-a-custom-url-scheme-for-your-app).

On a Mac with the production Xcode release containing the iOS 26 SDK:

```bash
xcodebuild \
  -project ios/PhoneLinkCompanion/PhoneLinkCompanion.xcodeproj \
  -scheme PhoneLinkCompanion \
  -configuration Debug \
  -destination 'generic/platform=iOS Simulator' \
  CODE_SIGNING_ALLOWED=NO \
  build
```

Open the project in Xcode, select a personal development team, and run on a physical iPhone for camera, local-network, Keychain, Photos, Contacts, Bluetooth, and `tel:` verification. See [Phase 4](docs/phase-4-ios-companion.md).

## Security rules

- Never enter or transmit an Apple Account password.
- Keep supported platform integrations distinct from experimental protocol research. The owner authorized reverse engineering of their own iPhone on November 2; that does not authorize targeting other people's devices, credential extraction, security bypasses, or unconfirmed destructive operations. Undocumented interfaces must be labeled and verified, not presented as Apple-supported APIs.
- Treat every optional Bluetooth profile as unavailable until runtime discovery succeeds.
- Keep private keys in iOS Keychain or Windows local-user DPAPI protection, never in configuration files or plaintext SQLite.
