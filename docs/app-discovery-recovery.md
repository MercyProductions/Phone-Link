# App-owned discovery recovery

Phone Link can restore its own missing Bonjour/DNS-SD advertisement. This is app connection recovery, not phone hardware repair, a USB Trust reset, or proof that an iPhone is reachable.

## Policy and controls

The Troubleshooter offers **Repair missing app discovery**. After pairing-server initialization, background maintenance also checks every 30 seconds. Both use the same policy and discovery lifecycle lock:

1. Capture lock state, trusted-companion count, eligible local-address count, and tracked native registration state.
2. Do nothing while locked, without a trusted companion, or without an eligible local network. USB presence is not companion pairing.
3. Leave an already registered advertisement and healthy sessions alone.
4. If eligible and missing, register the app's discovery service at its existing server port. Preserve certificate, identities, database, trust, sessions, contacts, pending commands and attachment state.
5. Capture an after-state. Report `RecoveredLocally` only if registration succeeded and prerequisites still hold. Failure, cancellation, lost prerequisites and an unavailable after-state are not successful repairs.

Each recovery attempt has a 25-second cancellation deadline; native teardown can finish later under the existing bounded-wait cleanup policy. After three unsuccessful recovery attempts or maintenance exceptions, automatic recovery waits at least five minutes since the most recent failure before another attempt; further failures keep the slower cadence. The cooldown uses elapsed time, not the Windows calendar clock. Missing prerequisites still suppress registration. A network-address change, a verified healthy result, or app restart restores the initial budget. The manual button remains available. This budget is separate from the pre-existing initial startup/network-change registration path. Background maintenance stops with app-service teardown. The last attempted recovery is held in memory and displayed separately from the copied read-only scan report. No payload, phone identifier or pairing material is logged.

`AdvertisementRegistered` means this process received a successful native registration callback and has not begun teardown. It is not a fresh multicast query, remote discovery proof, firewall test or authenticated companion session. A silently stale external mDNS state may need further diagnosis; the existing network-address-change path rebuilds registration. Recovery does not restart the pairing listener or forcibly reconnect a companion.

## Silent companion connections

The Windows `/sync/v1` WebSocket uses native PING/PONG keep-alive (20-second interval, 45-second reply timeout). This applies to the sync socket only; it does not change the pairing endpoint or the application's explicit round-trip diagnostic. A transport timeout aborts the socket and removes only that exact session from the active table, leaving stored trust intact. The companion can reconnect using its existing foreground-only retry path. Silence is not a hardware diagnosis, and the timer is not a guaranteed end-to-end deadline under host scheduling or receive backpressure.

A real TLS/WebSocket loopback test with shortened timings verifies that an authenticated client which stops reading is removed without revocation. The same trusted identity authenticates again and remains connected while its idle receive pump answers control frames. This verifies Windows transport behavior, not iOS foreground reconnection, background behavior, or a physical Wi-Fi interruption. See [ASP.NET Core WebSocket keep-alive timeout](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets?view=aspnetcore-10.0#keep-alive-timeout) and [Apple's RFC 6455 URLSession WebSocket transport](https://developer.apple.com/documentation/foundation/urlsessionwebsockettask).

## Verification and remaining gates

- Eighteen application tests cover recovery, before/after evidence, healthy no-op, locked/unpaired/offline refusal, action failure, missing after-probe, prerequisite changes, cancellation, retry limits, concurrent failure accounting and rearming.
- Original policy checkpoint: full solution Debug build with zero warnings/errors; 185 .NET tests passed at that point.
- Runtime Windows UI: four healthy Apple USB nodes, two eligible addresses, zero trusted/authenticated companions. Manual recovery returned `NotApplicable`, `ActionAttempted=False`; it did not create trust or claim repair.
- Runtime phone-information CLI/UI: four basic metadata fields available; all three battery-domain fields returned `GetProhibited`. No phone change was sent. Personal metadata values are excluded from these public notes.
- Successful native registration recovery with a real trusted companion, actual iPhone rediscovery/authentication afterward, forced native callback failure, and physical lock/network-change/exit races still require integration/hardware tests. Controlled policy tests do not prove those end-to-end cases.

### November 3 native recovery check

`NativeDiscoveryRecoveryTests` exercises the production recovery policy with real Windows DNS-SD registration and browsing. It uses a unique temporary service name, an ephemeral reserved loopback port, and synthetic trust held only in test memory; it never adds a peer to the owner's database. The test observes the unique name absent, restores the missing registration, observes the matching name/port through the Windows browser, verifies a healthy no-op, disposes only its fixture registration, and restores it again. Final cleanup disposes the fixture and closes its listener.

The focused native test and full 355-test .NET suite passed; full solution Debug build had zero warnings/errors. This proves callback-confirmed local recovery and a Windows browser observation, not fresh on-wire multicast, remote iPhone visibility, identity authentication, or end-to-end reconnection. The second recovery is callback-checked rather than independently browsed again. Disposal clears tracked registration immediately; native teardown may finish later under the existing bounded-wait policy. The synthetic trusted peer only exercises eligibility and is not evidence of a real paired companion.

This implements one bounded automatic app-owned recovery. Battery health, charging hardware, boot failures, service/driver repair, deeper authorized diagnostics, and the full accessory/SSH/runtime-dumper goal remain open.
