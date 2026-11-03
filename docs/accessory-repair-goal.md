# Active accessory, repair, tunnel, and offset-dumper goal

User goal (November 2): preserve the existing project and expand toward most useful Apple Watch/AirPods-style phone interactions, self-diagnosis and automatic repair where practical, SSH-style connectivity, and an offset dumper. This file is a checkpoint, not a narrowed definition of success or a claim that the full goal is achieved.

## Requirements and current evidence

| Track | Required proof | Current evidence / next work |
|---|---|---|
| Watch/AirPods-style interaction | Working calls/audio/microphone routing, notification presentation/actions, applicable message replies, media controls, connection/battery status, and user-triggered phone actions, assessed individually | Existing Bluetooth service detection and ANCS session are reusable; service discovery is not proof of an operation. Actual calls, audio routing, message-profile operations, media controls, and action verification remain incomplete. Do not claim a majority has shipped. Privileged Apple ecosystem features need separate entitlement/licensing/protocol research. |
| Self-diagnosis | Actual phone/Windows evidence, source/permission distinctions, reproducible symptom tests, structured findings, safe reports | Implemented Troubleshooter + explicit USB inventory/basic-info probes. Real phone metadata read successfully. Battery-domain values remain unknown on the tested no-session path. Crash/log analysis and deeper service-specific diagnosis remain next work. |
| Automatic repair | A justified diagnosis, reversible action where possible, explicit risk classification, before/after checks, and regression tests demonstrating the issue was resolved | Current scans are read-only, not repair. Next: app-owned session/discovery recovery actions; investigate supported service/driver repair separately. Hardware faults, resets, firmware writes and erasure are not generic automatic fixes. Never market a diagnostic hint as successful repair. |
| SSH/tunnel connection | Verify target service, loopback-only forwarding, explicit lifetime/stop, target pinning, bounded streams, disconnect cleanup, SSH host-key/authentication handled correctly | Added an explicit `--ssh-check` for standard port 22 via USB. On the attached phone the transport returned response code 3 and no SSH server was identified. No login/password/command was attempted. General port forwarding, tunnel UI, and an authenticated SSH session are not implemented yet. Do not confuse a USB transport connection or banner with SSH authentication. Investigate a suitable owned-device SSH endpoint independently of the tunnel, without silently changing security settings. |
| Offset dumper | Identify target image/build/UUID/architecture, distinguish file offsets, unslid virtual addresses and runtime addresses, validate bounds and reproducible fixtures; test actual target artifacts when available | Research references established below. Parser/dumper implementation is not yet present. Start with explicit user-selected Mach-O images/symbols; separately investigate runtime process/kernel access. Static analysis must not be presented as a live kernel/process dump. Stripped/encrypted/compressed images and ASLR require explicit support and evidence, not guessed offsets. |
| Existing expanded vision | Preserve secure trust, contacts, files/media, multi-device, discovery, privacy, screen research, packaging and hardware validation requirements | [Expanded vision](expanded-vision-roadmap.md) and [toolbox map](iphone-toolbox.md) remain active. No original requirement is considered satisfied merely because the new USB probes work. |

## Experiment boundaries

The owner authorized reverse engineering and undocumented interface research on their own iPhone. Do not infer authority over unrelated devices/accounts or treat protocol access as permission to read all personal data. The implemented probes request only minimal transport inventory and allowlisted non-unique metadata. No pairing credentials are copied, no lock/passcode/Activation Lock bypass is attempted, and no phone state/firmware has been changed by these experiments. Consequential new operations require their own clearly explained approval before execution.

## Next implementation order

1. Ship and retain the verified USB/Troubleshooter milestone with the actual phone evidence and test results.
2. Add structured per-field service failures, explicit authorized trust/session support, and app-owned recovery with before/after verification.
3. Implement managed loopback USB forwarding with device pinning and stop/cancellation/disconnect tests; only advertise SSH when a usable endpoint and authentication are actually verified.
4. Implement a bounded Mach-O offset/symbol inspector with image identity and file/virtual mapping, then investigate runtime dumping as a separate access-dependent feature. Do not relabel static offsets as complete fulfillment of the runtime interpretation.
5. Continue real accessory operations (ANCS actions, HFP/audio, MAP/PBAP, media controls) and large-file/media work. Maintain physical-device gates and explicit unsupported/unknown states.

## Primary research

- [libusbmuxd iproxy](https://github.com/libimobiledevice/libusbmuxd/blob/master/docs/iproxy.1) and [implementation](https://github.com/libimobiledevice/libusbmuxd/blob/master/tools/iproxy.c): USB forwarding is transport, not installation of an SSH server.
- [Apple Mach-O format header](https://github.com/apple-oss-distributions/xnu/blob/main/EXTERNAL_HEADERS/mach-o/loader.h): image/load-command structures for an offline inspector.
- [Apple symbolication and ASLR](https://developer.apple.com/videos/play/wwdc2021/10211/): image layout, symbol addresses and slide must be distinguished.
- [Experimental USB diagnostics and actual observations](experimental-usb-diagnostics.md).

The complete goal remains active and unproven. In particular, automatic phone repair, majority accessory parity, a working SSH tunnel/session, and the offset dumper must not be marked complete at this checkpoint.
