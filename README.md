# Phone Link

A real Windows + iPhone integration project built only on documented Apple, Microsoft, and open standards.

## Current status

The Windows app builds and launches on this Windows 11 workstation. A signed x64 development MSIX is available at `artifacts/windows/msix/PhoneLink-x64.msix`, but predates the current changes. On October 20, all 80 .NET tests passed and the companion compiled with all 8 tests passing in [native Xcode 26.3/iOS 26.2 simulator CI](https://github.com/MercyProductions/Phone-Link/actions/runs/39374852047). Physical-iPhone and signed-device verification remain open.

Implemented:

- one-time QR pairing with TLS SPKI pinning, Ed25519 device identities, HMAC client proof, server proof, and six-word human comparison;
- mutually authenticated WSS sessions and Bonjour/DNS-SD reconnect discovery;
- a live PC network-interface inventory, bounded private IPv4/IPv6 pairing endpoints, and current trusted-device connection status;
- Windows DNS-SD discovery for Phone Link, AirPlay, and network-audio advertisements, displayed as unverified service observations;
- local-user DPAPI secret protection, encrypted SQLite, per-file AES-GCM photo encryption, and iOS Keychain identities;
- authorized Contacts sync with per-PC protected outboxes, authoritative recovery checkpoints, permission-subset reconciliation, and idempotent Windows receipts;
- user-selected Photos import with chunk/final hashes, encrypted staging, bounded-memory PLA2 storage/export, and legacy import compatibility;
- live Apple Notification Center Service capability detection and an in-memory ANCS GATT session when Windows exposes it;
- native Windows app notifications for new, non-silent ANCS events, cleared when the session ends or the app locks;
- live MAP/PBAP capability checks without assuming that Windows exposes those profiles;
- user-confirmed click-to-call: Windows requests a number, then iPhone requires a tap before opening `tel:`;
- immediate trusted-device revocation and live-session termination;
- close-to-tray operation with an explicit Exit command and opt-in Windows Hello protection at startup, after five minutes without interaction, on tray hiding, and on Windows session lock/suspend;
- modern WinUI pages for Messages, Contacts, Photos, Calls, Notifications, Devices, Settings, and About;
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

The [expanded-vision roadmap](docs/expanded-vision-roadmap.md) tracks the October requirements, current gaps, and verification evidence. The existing installer predates the current discovery changes. Direct executable launches now use `%LOCALAPPDATA%/MercyProductions/PhoneLink` for protected local data; installed packages retain their package-local storage. Scoped link-local IPv6 addresses are displayed as interface metadata but excluded from QR offers because Windows interface scope IDs cannot be reused on iPhone.

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
.\packaging\Install-LocalMsix.ps1
```

The installer prompts before trusting the local public certificate and before installing the package. The build script creates a temporary private key in the current-user certificate store, signs the MSIX, exports only the public `.cer`, and removes the private certificate. This development certificate is for local sideload testing only; public distribution requires Microsoft Store signing or a production code-signing identity.

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
- Never use private Apple APIs, jailbreaks, credential extraction, or reverse-engineered Apple services.
- Treat every optional Bluetooth profile as unavailable until runtime discovery succeeds.
- Keep private keys in iOS Keychain or Windows local-user DPAPI protection, never in configuration files or plaintext SQLite.
