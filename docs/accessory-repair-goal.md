# Active accessory, repair, tunnel, and offset-dumper goal

User goal (November 2): preserve the existing project and expand toward most useful Apple Watch/AirPods-style phone interactions, self-diagnosis and automatic repair where practical, SSH-style connectivity, and an offset dumper. This file is a checkpoint, not a narrowed definition of success or a claim that the full goal is achieved.

## Requirements and current evidence

| Track | Required proof | Current evidence / next work |
|---|---|---|
| Watch/AirPods-style interaction | Working calls/audio/microphone routing, notification presentation/actions, applicable message replies, media controls, connection/battery status, and user-triggered phone actions, assessed individually | Existing Bluetooth service detection and ANCS session are reusable; service discovery is not proof of an operation. Actual calls, audio routing, message-profile operations, media controls, and action verification remain incomplete. Do not claim a majority has shipped. Privileged Apple ecosystem features need separate entitlement/licensing/protocol research. |
| Self-diagnosis | Actual phone/Windows evidence, source/permission distinctions, reproducible symptom tests, structured findings, safe reports | Implemented Troubleshooter + explicit USB inventory/basic-info probes. Real phone metadata read successfully. Battery-domain values remain unknown on the tested no-session path. Crash/log analysis and deeper service-specific diagnosis remain next work. |
| Automatic repair | A justified diagnosis, reversible action where possible, explicit risk classification, before/after checks, and regression tests demonstrating the issue was resolved | Added bounded automatic and manual missing app-discovery recovery with before/after checks, eligibility guards, healthy no-op and tested retry budget. Live UI verified refusal when no trusted companion exists; successful native recovery with a real companion is still unverified. See [recovery evidence](app-discovery-recovery.md). Phone/service/driver/hardware repair remains open; scans alone are not repair. |
| SSH/tunnel connection | Verify target service, loopback-only forwarding, explicit lifetime/stop, target pinning, bounded streams, disconnect cleanup, SSH host-key/authentication handled correctly | Managed loopback forwarding, CLI, Windows UI, hashed target rechecks, cancellation, half-close and cleanup tests are implemented. A temporary forwarder actually completed a read-only phone-information round-trip in CLI and UI. Port 22 returned response code 3 in the explicit SSH check; no authenticated SSH session exists. Hardware unplug/replug/multi-phone tests remain open. See [tools and evidence](usb-tunnels-and-static-offsets.md). |
| Offset dumper | Identify target image/build/UUID/architecture, distinguish file offsets, unslid virtual addresses and runtime addresses, validate bounds and reproducible fixtures; test actual target artifacts when available | Bounded static Mach-O inspector, CLI, Windows table and JSON export implemented. Twenty parser tests and native ARM64/x64/universal comparisons with Apple's nm/otool passed. Static artifacts were also parsed on Windows. No live phone process/kernel memory has been dumped; runtime access and actual target firmware/artifact compatibility remain open. Encrypted/compressed/stripped images do not receive guessed offsets. |
| Existing expanded vision | Preserve secure trust, contacts, files/media, multi-device, discovery, privacy, screen research, packaging and hardware validation requirements | [Expanded vision](expanded-vision-roadmap.md) and [toolbox map](iphone-toolbox.md) remain active. No original requirement is considered satisfied merely because the new USB probes work. |

## Experiment boundaries

The owner authorized reverse engineering and undocumented interface research on their own iPhone. Do not infer authority over unrelated devices/accounts or treat protocol access as permission to read all personal data. The implemented probes request only minimal transport inventory and allowlisted non-unique metadata. No pairing credentials are copied, no lock/passcode/Activation Lock bypass is attempted, and no phone state/firmware has been changed by these experiments. Consequential new operations require their own clearly explained approval before execution.

## Next implementation order

1. Ship and retain the verified USB/Troubleshooter milestone with the actual phone evidence and test results.
2. Per-field information statuses and bounded app-discovery recovery are implemented. Next: verify successful native recovery with a trusted companion, investigate authorized service/session support, and add deeper diagnosis with justified repair actions. Battery fields actually return GetProhibited through the current no-session path; do not reinterpret that as hardware failure.
3. Extend managed loopback USB forwarding with physical disconnect/reconnect and multiple-phone validation; investigate a usable owned-device SSH endpoint and normal host-key/authentication without weakening security.
4. Expand static Mach-O compatibility against explicit target artifacts and investigate runtime dumping as a separate access-dependent feature. Do not relabel static offsets as complete fulfillment of the runtime interpretation.
5. ANCS action commands, confirmation UI, characteristic checks and stale-state protection are implemented; [live testing](ancs-actions.md) found Allowed service access but AccessDenied characteristic enumeration. Resolve access and verify actual phone effects without claiming completion. Continue HFP/audio, MAP/PBAP, media controls and large-file/media work, with physical-device gates and explicit unknown states.

## Primary research

- [libusbmuxd iproxy](https://github.com/libimobiledevice/libusbmuxd/blob/master/docs/iproxy.1) and [implementation](https://github.com/libimobiledevice/libusbmuxd/blob/master/tools/iproxy.c): USB forwarding is transport, not installation of an SSH server.
- [Apple Mach-O format header](https://github.com/apple-oss-distributions/xnu/blob/main/EXTERNAL_HEADERS/mach-o/loader.h): image/load-command structures for an offline inspector.
- [Apple symbolication and ASLR](https://developer.apple.com/videos/play/wwdc2021/10211/): image layout, symbol addresses and slide must be distinguished.
- [Experimental USB diagnostics and actual observations](experimental-usb-diagnostics.md).

The complete goal remains active and unproven. In particular, automatic phone repair, majority accessory parity, an authenticated SSH session, and runtime process/kernel offset dumping must not be marked complete at this checkpoint. Static offset inspection and USB forwarding are implemented milestones, not substitutes for those remaining requirements.
