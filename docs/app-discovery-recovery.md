# App-owned discovery recovery

Phone Link can restore its own missing Bonjour/DNS-SD advertisement. This is app connection recovery, not phone hardware repair, a USB Trust reset, or proof that an iPhone is reachable.

## Policy and controls

The Troubleshooter offers **Repair missing app discovery**. After pairing-server initialization, background maintenance also checks every 30 seconds. Both use the same policy and discovery lifecycle lock:

1. Capture lock state, trusted-companion count, eligible local-address count, and tracked native registration state.
2. Do nothing while locked, without a trusted companion, or without an eligible local network. USB presence is not companion pairing.
3. Leave an already registered advertisement and healthy sessions alone.
4. If eligible and missing, register the app's discovery service at its existing server port. Preserve certificate, identities, database, trust, sessions, contacts, pending commands and attachment state.
5. Capture an after-state. Report `RecoveredLocally` only if registration succeeded and prerequisites still hold. Failure, cancellation, lost prerequisites and an unavailable after-state are not successful repairs.

Each recovery attempt has a 25-second cancellation deadline; native teardown can finish later under the existing bounded-wait cleanup policy. Automatic retries pause after three unsuccessful recovery attempts or maintenance exceptions. A network-address change, a verified healthy manual result, or app restart rearms the budget. The manual button remains available. This budget is separate from the pre-existing initial startup/network-change registration path. Background maintenance stops with app-service teardown. The last attempted recovery is held in memory and displayed separately from the copied read-only scan report. No payload, phone identifier or pairing material is logged.

`AdvertisementRegistered` means this process received a successful native registration callback and has not begun teardown. It is not a fresh multicast query, remote discovery proof, firewall test or authenticated companion session. A silently stale external mDNS state may need further diagnosis; the existing network-address-change path rebuilds registration. Recovery does not restart the pairing listener or forcibly reconnect a companion.

## Verification and remaining gates

- Eighteen application tests cover recovery, before/after evidence, healthy no-op, locked/unpaired/offline refusal, action failure, missing after-probe, prerequisite changes, cancellation, retry limits, concurrent failure accounting and rearming.
- Full solution Debug build: zero warnings/errors. Full .NET suite: 185 passed.
- Runtime Windows UI: four healthy Apple USB nodes, two eligible addresses, zero trusted/authenticated companions. Manual recovery returned `NotApplicable`, `ActionAttempted=False`; it did not create trust or claim repair.
- Runtime phone-information CLI/UI: four basic metadata fields available; all three battery-domain fields returned `GetProhibited`. No phone change was sent. Personal metadata values are excluded from these public notes.
- Successful native registration recovery with a real trusted companion, actual iPhone rediscovery/authentication afterward, forced native callback failure, and physical lock/network-change/exit races still require integration/hardware tests. Controlled policy tests do not prove those end-to-end cases.

This implements one bounded automatic app-owned recovery. Battery health, charging hardware, boot failures, service/driver repair, deeper authorized diagnostics, and the full accessory/SSH/runtime-dumper goal remain open.
