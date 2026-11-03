# iPhone toolbox for Windows

Scope added November 2, 2026: explore the full useful toolset, including diagnosis and repair assistance, not just Phone Link parity. Investigate routes independently and implement what evidence supports. A feature is not excluded merely because it needs an iPhone companion, an official external Apple tool, licensing, or additional research. Those dependencies must be explicit.

The owner subsequently explicitly authorized reverse engineering and undocumented interface research on their own phone in this chat. This supersedes the earlier public-API-only research restriction for that owned-device work; it does not turn experimental interfaces into officially supported APIs or authorize targeting other people's devices. Credential extraction, security bypasses, jailbreak/firmware changes, erasure, and other consequential operations are not implied by permission to investigate. This document records scope; it is not independent authorization for future operators or unrelated devices.

## Implemented first milestone

The Windows **Troubleshooter** navigation opens a live, read-only connection scan:

- Enumerates present Windows device nodes through `Windows.Devices.Enumeration`, matches Apple USB vendor hardware IDs, and reads the documented Windows problem flag. Device-node count is not physical-phone count or proof of iPhone identity, iOS Trust, hardware health, or charging performance.
- Checks current-user Apple Devices package registration; cannot substitute that for driver health, USB Trust, an app update check, or legacy iTunes detection. Access failures are unknown, not missing.
- Reuses the existing local-interface catalog and private pairing endpoint policy; eligible addresses are not proof of reachability or correct firewall configuration.
- Reads actual trusted companion and authenticated live-session counts from the existing application services. USB, OS Bluetooth pairing, Apple's USB Trust, and the companion's QR trust are distinct relationships.
- Produces pass/attention/unknown findings with next steps. Cancellation/navigation guards reject stale results. No repair runs on page load or during a scan.
- Copies a deliberately minimal report on an explicit user action: timestamp, counts, generic findings, and next steps; no device names, serials, USB instance IDs, addresses, QR secrets, contacts, logs, or phone content. Opening Apple Devices is a separate button; the application does not automate its backup/update/restore controls.

No firmware update, restore, driver replacement, security-setting change, Trust approval, hardware test, or service restart is performed by this milestone. Normal service initialization already used by Phone Link may occur while checking companion sessions. There is no claim that physical-phone repair is implemented.

## Expansion map

| Tool area | Useful capabilities to pursue | Route and next proof |
|---|---|---|
| USB / Windows connectivity | Detect connection changes, distinguish driver errors, identify USB mode when reliably exposed, correlate port/driver problems, help establish USB Trust | Live read-only scan implemented. Next: device-change watcher, specific problem codes, presence history, and physical-phone positive/negative tests. Never label an unknown USB device as an iPhone by guesswork. |
| Pairing / wireless repair | Pinpoint discovery vs authentication failures, verify local reachability, rebuild only app-owned stale connection state, retry without deleting trust, multi-device diagnostics | Existing authenticated server/reconnect plus new session scan. Next: structured failure evidence and explicit, reversible repair actions; do not silently re-pair changed identities or disable a firewall. |
| Backup and migration | Guide encrypted Apple Devices backups, inventory user-selected local backup folders, inspect supported metadata, check consistency, plan migration and recovery | Apple documents Windows backups. Official-app launch is implemented; backup inventory/consistency checking is not. Research published formats/interfaces before direct integration. A consistency check is not proof a backup restores successfully. Never guess or bypass an encrypted-backup password. |
| Crash / reboot / performance analysis | Import user-shared `.ips` or crash reports, group repeated incidents, explain documented exception/termination information, correlate times and symptoms, create a redacted support report | [Local type-309 JSON analysis](crash-report-analysis.md) now implements bounded parsing, allowlisted signatures, category-specific guidance, redacted output and a picker UI, with 37 synthetic tests. Successful UI import/copy and real owner reports remain unverified. Panic/JetSam formats, repeated-incident grouping and time correlation remain next work. Do not infer a defective board/component from a signature or claim access to all system logs over USB. |
| Companion health checks | Permission diagnostics, connection quality/throughput, app storage pressure, supported battery/charging/thermal observations, interactive camera/microphone/speaker/touch checks | [Explicit health snapshots](companion-health-snapshots.md) now capture charge/thermal/power and permission states, keep storage local, and share only after per-snapshot confirmation to the connected PC. Native simulator CI and Windows empty state verified; physical delivery/readings and interactive hardware checks remain open. USB detection cannot measure cable wattage or battery maximum capacity. |
| Boot / update / recovery | Symptom-specific decision trees, backup preflight, official error-code explanations, update-first guidance, recovery-mode instructions and post-recovery verification | Apple Devices / Apple Recovery Assistant are supported user-facing routes. Next: model/OS-specific workflows and explicit risk gates. A generic safe, public third-party firmware repair API has not been established. No automatic Restore: it erases data. |
| Hardware diagnosis / repair completion | Surface model-appropriate Apple diagnostics; guide official part-calibration/Repair Assistant workflows and record user-approved results | Apple's diagnostics and Repair Assistant have their own eligibility and user controls. Next: current compatibility mapping and guided handoff; do not assume our Windows app can remotely invoke proprietary test suites, calibrate parts, or repair physical damage. |
| Files / photos / data rescue | Bidirectional authorized files, large videos, resume, duplicate detection, thumbnails, selective export, integrity checks, migration of accessible content | Existing selected-photo/contact paths are reusable. PLA2 encrypted staging/export is in place; generic files/resume remain unfinished. Rescuing accessible or backed-up data is different from recovering erased/encrypted storage without keys. |
| Calls / notifications / messaging | User-authorized Bluetooth profiles, source-correct notification organization/actions, practical call routing and permitted replies | Existing public service probes and ANCS session; implement actual profile operations only after transport proof. Discovering MAP does not prove working message sending or history access. Companion limitations do not automatically rule out separate accessory routes. |
| Screen / media / automation | Authorized companion screen capture/streaming/recording, user-triggered Shortcuts/App Intents, investigate AirPlay receiver licensing and other supported accessory paths | Revalidate public APIs, platform availability, licensing and actual device behavior per route. Mirroring is not remote control. No universal iOS UI automation claim. |

These are work tracks, not feature-completion claims. New discoveries should expand this table and the implementation rather than being forced into the first milestone's architecture.

## Action safety and evidence

1. **Read-only checks:** run locally, report what was actually observed, preserve unknown states, collect the minimum data needed.
2. **App-owned recovery:** explicit actions with verifiable preconditions and a result check. Do not delete trust, outboxes or cached personal data as a generic repair.
3. **Windows service/driver changes:** a separate confirmed action, explain affected devices, require appropriate privileges, prefer official signed components, and never weaken security to make a test pass.
4. **iPhone recovery/restore:** user-controlled Apple workflow, explain backup prerequisites and data-loss risk immediately before any destructive step. The current implementation only opens the official app/guidance.
5. **Hardware conclusions:** retain observations and confidence, offer repeatable tests, and escalate unsupported repair operations to the appropriate official workflow rather than fabricate an automatic fix.

## Verification, November 2

- `dotnet build PhoneLink.slnx --no-restore --configuration Release -v quiet`: passed, zero warnings/errors.
- `dotnet test PhoneLink.slnx --no-restore --configuration Release --no-build`: 96 passed (3 domain, 21 application, 15 protocol, 7 security, 8 persistence, 42 communication).
- New tests cover unknown vs absent evidence, Windows problem flags, no USB-to-companion trust inference, report redaction, vendor-prefix false positives, cancellation, and an actual read-only Windows probe.
- Runtime Windows UI inspection: Troubleshooter navigation, completed live scan, readable findings, Apple Devices installed, four eligible pairing addresses, zero authenticated companions, no Apple USB nodes in that snapshot, zero unavailable probes. No phone repair, permission/setting change, clipboard modification, or Apple Devices launch was performed during validation. Positive physical-iPhone USB/driver-failure tests remain open.
- The computer-use skill was used to verify the actual UI, revealing a refresh delay after navigation; a subsequent fresh capture confirmed the completed scan. Runtime observations, not a screenshot of a mockup, establish the page is integrated.
- The earlier QR external-link change also passed native iOS CI: [run 39375891304](https://github.com/MercyProductions/Phone-Link/actions/runs/39375891304). This does not prove physical Camera recognition or signed device installation.

## Primary references

The later owner-authorized experimental milestone is documented in [USB diagnostics](experimental-usb-diagnostics.md), and the complete new goal in [accessory/repair/tunnel/dumper work](accessory-repair-goal.md). It adds a device-probe CLI to the solution, read-only local transport queries, actual phone metadata reads, and a bounded SSH availability check. The latest Debug build passed with all 130 .NET tests. After the cable swap, runtime UI checks saw four healthy Apple USB nodes and successfully used both experimental buttons against one attached iPhone; basic metadata was shown and unavailable battery observations remained unknown. No credential, data-changing phone, or SSH login operation was attempted. The earlier 96-test/zero-USB observations above record the initial milestone, not the current phone connection state.

- [Microsoft device information properties](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/device-information-properties) — present/device/problem/hardware-ID evidence.
- [Microsoft current-user package inventory](https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagemanager.findpackagesforuser?view=winrt-26100).
- [Apple computer recognition troubleshooting](https://support.apple.com/108643) and [USB Trust](https://support.apple.com/109054).
- [Apple Windows backups](https://support.apple.com/en-us/108967).
- [Apple crash-report and device-log analysis](https://developer.apple.com/documentation/xcode/diagnosing-issues-using-crash-reports-and-device-logs).
- [Apple recovery/update/restore](https://support.apple.com/118106), [Recovery Assistant](https://support.apple.com/en-gb/123114), [diagnostics mode](https://support.apple.com/101944), and [Repair Assistant](https://support.apple.com/en-nz/120579). Eligibility and exact steps must be rechecked when implementing each workflow.
