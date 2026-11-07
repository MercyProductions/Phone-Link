# Experimental USB diagnostics

Owner-authorized research, November 2, 2026. This is independent implementation of observed Apple local/device protocol behavior, not an Apple public SDK or a promise of compatibility with every iOS version. No third-party binary or vendor DLL was modified or installed.

## Implemented operations

1. **Local inventory:** connect only to `127.0.0.1:27015`, send one bounded plist `ListDevices` request, and return USB/network/other connection counts. No port scanning, pairing record requests, device identifiers in results, or device commands. A four-second deadline and 1 MiB response bound apply.
2. **Basic USB information:** obtain the live inventory, require exactly one USB-connected device, and use its ephemeral transport ID to open the fixed lockdownd information port through the local service. This is channel setup, not a pairing operation. Then issue only allowlisted `GetValue` requests:
   - `DeviceClass`, `ProductType`, `ProductVersion`, `BuildVersion`, with no domain.
   - `BatteryCurrentCapacity`, `BatteryIsCharging`, `ExternalConnected`, only in `com.apple.mobile.battery`.

The information probe has a twelve-second overall deadline and a 64 KiB per-response bound. It does not issue a bulk GetValue query, read pairing credentials, start a trusted session, launch phone services, write values, reboot, update, restore, or attempt to bypass a locked/protected state. Unsupported/protected values remain unknown. It refuses ambiguous multi-USB-device selection rather than silently choosing a phone. Device unplug/replug during selection can invalidate the ephemeral ID and cause the read to fail; users should keep the selected device connected throughout the explicit check.

The UI exposes both probes as separate **Experimental** buttons under Troubleshooter. Basic information is displayed with a read timestamp, cleared on navigation/rescan, and excluded from the minimal copied connection report. The report may include generic experimental inventory counts, not raw XML, phone names, UDIDs, serials, or pairing material.

XML parsing ignores DTD declarations without resolving external resources, rejects duplicate dictionary keys, and validates framing/version/message/tag. Returned serials present in the local inventory are not exposed in result objects; raw payload byte buffers are cleared after parsing. This is not a claim that managed-memory dumps cannot contain transient strings.

## Explicit command-line probes

An additional separate probe now reads only `DeveloperModeStatus` in `com.apple.security.mac.amfi`. It is not part of the normal seven-field information scan. Run `--developer-mode-status` explicitly, or **USB & binary tools → Read Developer Mode status**. A six-second overall deadline and the existing 64 KiB framing bound apply. The domain/key pair is exact and allowlisted; no SetValue, enable/arm/reveal/confirm operation, reboot, image mount, pairing-record request, StartSession or service launch is issued. Strict plist booleans yield a reported enabled/disabled value; denial, missing or unexpected values remain unknown. Device errors use the same fixed-name filtering as the other information reads.

November 3 physical follow-up: inventory still reported one USB device, SSH port 22 was still rejected with transport code 3, and both the new CLI probe and live Windows UI reported Developer Mode as unknown / `DeviceReportedError` / `GetProhibited`. This is not evidence that the mode is disabled or that the phone has a fault. [The independently checked reference](https://github.com/libimobiledevice/libimobiledevice/blob/fa0f79190142bc309307967c058f89c1b36eb6b8/tools/idevicedevmodectl.c) uses an authenticated lockdownd handshake before reading this field; our intentional no-session path cannot prove whether authorized access would succeed. No source/vendor code was copied, and no credentials or trust records were used.

Developer Mode is only one development prerequisite, not SSH, root access, signing authorization, debugger entitlement, decrypted image access or permission to inspect arbitrary processes/kernel memory. [Apple's Developer Mode instructions](https://developer.apple.com/documentation/xcode/enabling-developer-mode-on-a-device) describe a separate user-controlled security decision and restart; this application does not perform it. The owner was asked only to inspect the current setting without changing it. USB GetProhibited is also separate from the unresolved Bluetooth characteristic AccessDenied result; no common cause is established.

Verification: eight added parser/request tests cover the exact read shape, disallowed keys/domains, real true/false values, invalid flag encodings and denied/error redaction. Full solution: 354 tests passed; build zero warnings/errors. The computer-use skill verified the new control and actual protected-response rendering. Authenticated mode reads and runtime debugging remain unverified.

From the repository root, with an owned phone attached:

```powershell
dotnet run --project tools/PhoneLink.DeviceProbe/PhoneLink.DeviceProbe.csproj -- --inventory
dotnet run --project tools/PhoneLink.DeviceProbe/PhoneLink.DeviceProbe.csproj -- --info
dotnet run --project tools/PhoneLink.DeviceProbe/PhoneLink.DeviceProbe.csproj -- --ssh-check
dotnet run --project tools/PhoneLink.DeviceProbe/PhoneLink.DeviceProbe.csproj -- --developer-mode-status
```

The tool also now accepts `--verify-tunnel` and `--forward <device-port>`; see [managed tunnels and static inspection](usb-tunnels-and-static-offsets.md) for their separate scope and limits. There is no arbitrary command, key/domain, host, restore, or firmware argument. Default automated tests exercise parsers and the read-only local inventory; they do not automatically run basic phone-information or SSH reads on arbitrary connected hardware. The `--info` command and UI button explicitly request information reads. `--ssh-check [device-port]` explicitly attempts only one selected port (default 22) through the selected USB connection, sends an SSH 2.0 identification if it connects, reads a bounded identification, then closes; it does not authenticate, verify a host key, send commands, or start a persistent tunnel. A positive banner alone is not authenticated SSH access.

The Troubleshooter and `--connection-check` also query the bounded local inventory with `ListDevices`, including when the user has started the live USB monitor and a scan is triggered. The normal report includes only the aggregate USB count or a fixed classification (refused, timed out, unsupported reply, or unavailable). It never opens a phone channel, reads a pairing record, restarts a Windows service or publishes the inventory's device identifiers. Windows PnP nodes and local transport entries are distinct observations. An empty successful reply is different from a failed query; only known mismatches contribute to repeated-attention guidance. This check does not depend on the service display name and cannot prove USB Trust or media access.

## Physical evidence on November 2

- Before the owner's cable swap: Windows enumerated no present Apple USB nodes and the responding local Apple transport reported `USB=0, network=0, other=0`.
- After the owner changed from USB-C–USB-C to USB-A–USB-C: Windows reported Apple iPhone, Apple Mobile Device USB Composite Device, Apple Mobile Device USB Device, and Apple Mobile Device Ethernet with status OK. The local transport reported `USB=1, network=0, other=0`. These four Windows device nodes are not four phones. This establishes the new data path works; it does not isolate whether the prior cable, port, adapter, or another factor caused the earlier failure.
- The responding loopback listener was owned by `AppleMobileDeviceProcess` under the current-user Apple Devices package installation.
- `--info` actually read device class, hardware identifier, iOS version, and build from the attached phone without pairing credential access. The specific owner-device values are kept out of this public repository's evidence log. These are reported metadata, not cryptographically authenticated hardware identity or proof of USB Trust. Parser tests use schematic fixtures rather than publishing a device inventory.
- All three tested battery observations were unavailable through this no-session query path. Follow-up per-field CLI and UI checks established `DeviceReportedError` / `GetProhibited` for each one. Values remain null/unknown; this is not a battery fault. The probe does not fabricate percentage, capacity, health, charging wattage, temperature or degradation estimates. Investigating a properly authorized diagnostics service is a separate next step.
- No data-changing phone command was sent. No physical iOS companion installation or new QR approval was performed by these probes.
- `--ssh-check` completed against the attached phone: the local transport rejected standard port 22 with response code 3; no SSH banner, login, or command was established. This is a result for that port and time, not proof no other endpoint could ever be configured.
- The Windows Debug UI actually ran the complete connection scan and both experimental buttons: four healthy Apple USB nodes, one USB transport connection, basic phone fields displayed, battery values unknown. The computer-use skill verified the live controls and scrollable page. Clipboard and Apple Devices launch controls were not activated during this validation.

## Per-field observations

The basic-information DTO/CLI now includes seven allowlisted field observations: `Available`, `DeviceReportedError`, `MissingValue`, or `UnexpectedValue`. Device errors retain only six recognized constant names (`MissingKey`, `MissingValue`, `GetProhibited`, `PasswordProtected`, `SessionInactive`, `InvalidHostID`); other or malformed error content becomes `OtherDeviceError`. Raw error text is never published. An explicit failed Result cannot be treated as a valid information value. Transport/framing failure still aborts the overall probe rather than fabricating individual values. Nine added tests cover these distinctions. Error-name references were checked against the [libimobiledevice implementation](https://github.com/libimobiledevice/libimobiledevice/blob/master/src/lockdown.c); this is experimental protocol behavior, not an Apple public API guarantee.

## Research references and next proofs

- [pymobiledevice3 local transport source](https://github.com/doronz88/pymobiledevice3/blob/71f2dab7f7f3d8f357912e07d4bdbbeb38756624/pymobiledevice3/usbmux.py): Windows endpoint, frame structure, inventory and channel setup. Protocol facts were used; vendor source was not copied into the application.
- [libimobiledevice GetValue interface](https://docs.libimobiledevice.org/libimobiledevice/latest/lockdown_8h_a204c585addfca987e09f8fda24a04b28.html) and [implementation reference](https://github.com/libimobiledevice/libimobiledevice/blob/master/src/lockdown.c).
- [tidevice's battery-domain examples](https://github.com/alibaba/tidevice/blob/main/README_EN.md): candidate read keys; availability is established only by actual device responses.

Next: per-field denial/unsupported reasons, a verified device picker for multiple phones, transport-disconnect recovery tests, explicit supported trust/session adapters, authorized crash-report import/analysis, and deeper diagnostics-service research. Maintain separate risk classification for read observations, launching a service, changing settings, reboot/recovery, and destructive repair. Do not market API reachability or a plausible panic-string explanation as successful repair.
