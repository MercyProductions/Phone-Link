# Expanded vision: implementation checkpoint

Objective source: the October 19, 2026 next-generation iPhone integration directive. The complete expanded scope remains active; this checkpoint records evidence and priorities, not completion of that scope.

## Current evidence

The repository contains 14 .NET solution projects, C# 14 / .NET 10, WinUI 3, Windows App SDK 2.3.1, encrypted SQLite, a Swift 6 / iOS 26 companion, tests, and development MSIX scripts. Existing trust and transport components should be extended rather than replaced. The August feasibility document is historical research and requires a fresh SDK/platform review before treating version-dependent claims as current.

Source inspection confirms QR trust, identity proofs, pinned TLS, WSS sessions, contact sync, selected-photo import, ANCS session parsing, Bluetooth profile probes, confirmed dial requests, revocation, and tray operation. Interface presence alone does not verify physical-iPhone behavior. Xcode compilation and hardware verification remain open.

Known gaps found during inspection:

- Windows had no network-interface display; the Devices page's badge represented trust, not a live connection.
- Leaving Devices retained its pairing offer, and asynchronous QR generation could restore an already-cancelled code.
- Numeric IPv6 scope IDs from Windows were exported in QR endpoints even though those interface IDs have no meaning on iPhone.
- The original Hello lock checked inactive duration only when the window activated. A timer/startup/OS-event implementation is now in place; real Hello and OS-session behavior still need interactive validation.
- Contacts outbox and sequence settings are installation-wide rather than per Windows peer; multiple-PC synchronization and permission downgrade handling require review.
- Transfers currently buffer bounded photo payloads; arbitrary files, large videos, streaming encryption, bidirectional delivery, resume, thumbnails, and duplicate-media detection are missing.
- The DNS-SD advertiser now retains callback/request lifetime through late native completion and serializes disposal. A never-completing native callback retains its rooted request until process exit instead of freeing memory still owned by Windows.
- Messages displays capabilities rather than conversation synchronization. MAP/PBAP transport implementations and ANCS actions are absent.

## Prioritized milestones

1. **Discovery and trust visibility:** network interfaces/addresses, current trusted-session state, lifecycle-safe pairing, receive-side Bonjour discovery with explicit unverified/verified identity, multi-device snapshots, network-change recovery. Do not port scan or identify phones solely by hostname.
2. **Security and recovery:** app-lock timer/startup/OS-session integration, native DNS-SD callback lifetime, per-peer durable sync, permission revocation, stale-session and reconnect tests. Keep secure pairing and at-rest protection intact.
3. **Authorized file/media sharing:** generic picker-based files and videos, bounded-memory transfer, progress, cancellation, encrypted staging, resumable offsets verified against hashes, duplicate detection, metadata, thumbnails, Windows drag/drop, and iPhone export/share controls.
4. **Companion health and dashboard:** timestamped name/model/OS/battery/network observations, explicit unknown/stale states, device-specific operation selection, permission settings, local disconnect/revoke.
5. **Accessory features:** hardware-verified ANCS actions and state reconciliation, application grouping, then MAP/PBAP feasibility proofs through public Windows interfaces. Separate message notifications, SMS, RCS, and iMessage in UX and capability negotiation.
6. **Screen/media integration:** revalidate production Apple screen-capture APIs and Windows decoding, implement explicitly authorized companion screen streaming when supported, full-screen playback and recording; investigate AirPlay receiver licensing separately. Mirroring does not imply remote input.
7. **Additional integration routes:** evaluate USB/Apple Mobile Device supported interfaces, custom BLE presence/control, App Intents/Shortcuts, WebRTC, and management eligibility individually. Only advertise implemented, authorized routes to the connection selector.
8. **Product and release:** responsive accessible dashboard/navigation, theme preference, polished file/media views, compatibility matrix, Mac CI compilation, physical-device test checklist, installer upgrade/uninstall behavior and updated signed distribution artifacts.

## Capability research extensions

| Route | Public boundary / next proof | Companion / authorization / background |
|---|---|---|
| IPv4/IPv6 + WSS | Existing authenticated transport; interface catalog now shares QR endpoint selection. Global IPv6 pairing policy and link-local bootstrap need further design. | Companion and explicit QR trust; iOS execution required. |
| Bonjour/mDNS | Windows advertiser, bounded Windows DNS-SD discovery, and iOS browser. Windows displays service name/type, host, port, addresses when exposed, and observation time. Service names are discovery hints, never device identity. | Local Network permission on iOS; discovery does not grant data access. |
| Bluetooth Classic/BLE | Existing paired-service probes and ANCS GATT; MAP/PBAP/actions still need real accessory tests. | System Bluetooth authorization; capabilities depend on exposed services. |
| USB / Apple Mobile Device | No supported generic Windows-to-iPhone data interface established yet. External Accessory documents MFi accessory communication, not arbitrary PC access. | Evaluate tethering, user sharing, and device trust independently; no private lockdownd/usbmux dependency is approved. |
| Screen streaming | ReplayKit and current ScreenCaptureKit documentation must be checked against actual SDK availability before selecting the implementation. | Explicit capture consent, companion, and interruption/lock handling; no remote-input claim. |
| AirPlay | Receiving on arbitrary Windows PCs has not been established as a freely implementable Apple public API. | Licensing and receiver interoperability proof required. |
| WebRTC | Candidate media/data transport after authenticated signaling; does not expand iOS data permissions. | Companion and media consent; background limits apply. |
| Shortcuts / App Intents | Candidate user-triggered actions for this app's own operations. | Companion and user action; does not expose other applications' private stores. |
| Device management | Separate managed-device use case, not an entitlement to inspect a personal iPhone. | Enrollment, platform eligibility, and explicit administration required. |

Sources checked October 19: [Microsoft NetworkChange](https://learn.microsoft.com/en-us/dotnet/api/system.net.networkinformation.networkchange?view=net-10.0), [Apple External Accessory](https://developer.apple.com/documentation/externalaccessory/), [Apple ReplayKit](https://developer.apple.com/documentation/replaykit), [Apple ScreenCaptureKit](https://developer.apple.com/documentation/screencapturekit). Search snippets alone do not establish platform or deployment-version availability.

## Current change and verification

Implemented local-interface capture and deterministic bounded QR address selection, IPv4 and IPv6 ULA support, exclusion of sender-scoped link-local IPv6 from QR, network-change display refresh, live trusted-session status, pairing invalidation on page unload/network change, and stale QR/approval guards. No probing traffic is sent by the catalog.

Runtime testing also exposed an unpackaged startup failure: package-only `Windows.Storage.ApplicationData.Current` was used unconditionally for both local data and settings. `LocalAppStorage` now queries package identity through documented `GetCurrentPackageFullName`, preserves packaged storage, and selects a per-user desktop directory for direct launches. The optional Hello preference uses a presence marker in that directory for unpackaged runs; no secrets are stored in that marker. Existing DPAPI encryption remains in place.

Verification on October 19:

- `dotnet build PhoneLink.slnx --no-restore`: zero warnings/errors.
- `dotnet test PhoneLink.slnx --no-build --no-restore`: 53 passed, including private-address selection/deduplication/limit tests and a default protected-store regression test outside package identity.
- Computer-use runtime inspection: direct executable launch, Devices page without initialization errors, actual network interfaces and addresses, Settings navigation without crashing, and return to Devices succeeded. No device consent or security settings were changed.
- No physical-iPhone session, QR approval, adapter disconnect, multi-device reconnect, or Xcode compilation was exercised in this milestone. Those remain required and must not be inferred from these checks.

## Discovery/recovery and lock hardening, October 20

Implemented `WindowsBonjourBrowser` using public `DeviceInformation` DNS-SD watchers, constrained to `_phonelink._tcp`, `_airplay._tcp`, and `_raop._tcp` in the local domain. Scans run on Devices page load, Refresh, and network change, and cancel on page unload. Each service observation is bounded/sanitized and explicitly unverified. Advertisements are never promoted into trusted devices and do not enable remote operations. No port scan is performed.

The native advertiser now supplies a cancellation handle and roots its delegate, request, and service instance until completion. Concurrent disposal shares one cleanup task. Late native cleanup is allowed to finish after the bounded caller wait. If Windows never delivers completion, retaining that native request until process exit is intentional; the application does not risk a late callback into freed memory. Network changes and revocation queue serialized advertisement updates that recheck whether trust and local addresses remain available.

The app lock now has a monotonic-time policy, one-second UI timer, handled pointer/key activity tracking, startup verification when enabled, tray-hide locking, and Windows session/suspend event subscriptions. New lock events invalidate outstanding verification versions. Failed/cancelled verification keeps the privacy overlay in place. Desktop verification uses `UserConsentVerifierInterop.RequestVerificationForWindowAsync` with the owning HWND rather than the UWP-only request path. Settings errors are handled without crashing. ANCS banner dispatch is serialized onto the UI thread and rechecks the owning live session and lock state so queued notifications cannot appear after locking or session teardown.

Verification:

- Solution build: zero warnings/errors.
- Full suite: 64 tests passed (3 domain, 11 application, 15 protocol, 7 security, 7 persistence, 21 communication).
- Real Windows integration tests registered a DNS-SD service and discovered its instance and port, cancelled a live scan and successfully rescanned, and concurrently disposed a native advertisement.
- Runtime UI inspection found a real nearby AirPlay advertisement with host/port/IPv4/IPv6 data and the unverified identity label. Refresh and navigation were exercised. A service advertisement proves discovery only; it does not prove streaming or identify an iPhone.
- The lock policy tests cover startup lock, activity reset before expiry, activity after expiry, session-lock invalidation, disabled policy, and rejection of stale verification results. Real Hello prompts, five-minute UI locking, physical suspend/session lock, physical iPhone reconnect after network transition, and release MSIX behavior remain manual gates.

Sources: [Microsoft network enumeration](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/enumerate-devices-over-a-network), [DNS service registration](https://learn.microsoft.com/en-us/windows/win32/api/windns/nf-windns-dnsserviceregister), [DNS service deregistration](https://learn.microsoft.com/en-us/windows/win32/api/windns/nf-windns-dnsservicederegister), [desktop WinRT UI ownership and UserConsentVerifier interop](https://learn.microsoft.com/en-us/windows/apps/develop/ui/display-ui-objects).

Next: complete the foreground companion reconnect loop and per-peer sync audit, implement generic large-file/media transfer, and investigate usable MAP/PBAP operations against the currently exposed public services. UI discovery of an RFCOMM service is not proof of a working message send/reply path. Existing signed MSIX predates these changes and must be rebuilt later. The full expanded goal remains active.
