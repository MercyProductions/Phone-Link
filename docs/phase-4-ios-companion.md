# Phase 4: iPhone companion

Phase 4 adds a native Swift 6 / SwiftUI companion project at `ios/PhoneLinkCompanion`. It uses Apple frameworks directly and does not embed a web runtime or a cross-platform compatibility layer.

## Implemented companion

- A conventional Xcode application target and shared scheme
- An iOS 26 deployment baseline, deliberately excluding beta-only iOS 27 APIs
- A SwiftUI home, pairing guidance, and privacy settings flow
- A per-install Ed25519 signing identity generated with CryptoKit
- Private-key persistence in iOS Keychain using `AfterFirstUnlockThisDeviceOnly`
- A short SHA-256 identity fingerprint for human verification
- Local-network, Bonjour, camera, and Contacts privacy descriptions
- A 1024×1024 opaque production app icon in the asset catalog
- XCTest coverage for deterministic identity fingerprint display
- VisionKit QR scanning and one-time authenticated pairing
- Pinned-TLS, mutually authenticated WebSocket reconnect sessions
- Bonjour discovery with trusted-endpoint fallback
- authorized Contacts sync with a file-protected durable retry outbox
- system PhotosPicker import with verified chunked transfer
- foreground, explicitly confirmed `tel:` dial requests from Windows
- a single receive router that correlates acknowledgements while accepting inbound commands

The private identity key never leaves Keychain. Only its 32-byte public key is exposed to the session model. Pairing and every later session prove possession of that identity.

## Files

The Xcode project is `ios/PhoneLinkCompanion/PhoneLinkCompanion.xcodeproj`. Application sources are organized under:

- `App` — composition root and session state
- `Features/Home` — companion status and privacy explanation
- `Features/Pairing` — QR scanner, transcript validation, and trust establishment
- `Features/Contacts` — least-field contact sync and durable retry outbox
- `Features/Photos` — user-selected photo import
- `Features/Settings` — explicit privacy preferences
- `Infrastructure/Security` — Keychain and CryptoKit identity/trust management
- `Infrastructure/Session` — Bonjour, pinned TLS, session authentication, routing, and binary frames
- `Resources` — privacy manifest values and app assets

`Resources/PrivacyInfo.xcprivacy` is included in the application target. It declares no tracking or developer data collection and records app-private `UserDefaults` access under Apple's approved `CA92.1` reason. Contacts and selected photos travel only over the authenticated local session to the user's paired PC; no developer service receives them. The App Store privacy answers must remain consistent with that architecture.

## Build on macOS

Install the production Xcode release that contains the iOS 26 SDK, then run from the repository root:

```bash
xcodebuild \
  -project ios/PhoneLinkCompanion/PhoneLinkCompanion.xcodeproj \
  -scheme PhoneLinkCompanion \
  -configuration Debug \
  -destination 'generic/platform=iOS Simulator' \
  CODE_SIGNING_ALLOWED=NO \
  build
```

For tests, first list installed simulator names:

```bash
xcrun simctl list devices available
```

Then select one of those names:

```bash
xcodebuild \
  -project ios/PhoneLinkCompanion/PhoneLinkCompanion.xcodeproj \
  -scheme PhoneLinkCompanion \
  -destination 'platform=iOS Simulator,name=iPhone 17 Pro,OS=latest' \
  test
```

To run on a personal iPhone, open the project in Xcode, select the PhoneLinkCompanion target, choose the owner's Apple development team, and replace `com.gabri.PhoneLinkCompanion` if that bundle identifier is unavailable. Camera, local-network, Keychain persistence, and future background behavior must also be verified on a physical iPhone.

## Verification boundary

This Windows workstation has no Swift, Xcode, or iOS SDK toolchain, so it cannot compile or sign an iPhone app. The project file delimiters, XML property lists, shared scheme, JSON asset catalogs, file membership, image dimensions/opacity, and absence of crash stubs were validated locally. A real Xcode build remains a required release gate; later phases must not claim physical-device verification until that command passes on macOS.
