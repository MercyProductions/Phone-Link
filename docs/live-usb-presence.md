# Live USB presence diagnostics

Open **Troubleshooter → Start live USB monitor** while comparing data cables or direct USB ports. This read-only Windows DeviceWatcher observes present device nodes and uses the same Apple USB vendor-prefix/hardware-ID classifier as the connection scan. It does not open a phone, read pairing records, send phone commands, restart services, change drivers or attempt a repair.

## Interpretation and privacy

Initial enumeration establishes a baseline, not reconnect events. Later matching node arrivals/removals update the present count and a latest-20-event history with local observation time, direction and count. Duplicate arrivals and unknown removals do not create events. Property updates are not interpreted as reconnects. One iPhone can expose several nodes, and driver activity can create node events: neither event count nor present count identifies physical phones, proves a cable failure, certifies charging quality, authenticates USB Trust or establishes hardware health.

Windows device keys are retained privately in memory only while monitoring, capped at 256 matching nodes. Keys/names/serials are not displayed, logged, copied or persisted by this feature. Public snapshots contain only counts, state and observation times. Exceeding the cap discards partial evidence and reports unavailable, not a healthy count. A native abort or initialization failure is also unknown, never zero. Initial readiness has a 15-second bound (the UI adds a 20-second cancellation deadline).

Stop, page unload and Phone Link lock cancel pending startup, stop the UI timer, discard keys/history and detach native callbacks. Native Stop is asynchronous; each instance is single-use and late callbacks cannot rearm stopped history. Explicit restart creates a new watcher. Lock also cancels pending Troubleshooter scans/imports and clears displayed crash/phone/connection/health data. Unlock does not automatically resume monitoring. History remains separate from the copied connection report; copying a report does not export this timeline.

This is an observation tool, not an automatic hardware diagnosis. For comparison, start monitoring, reconnect a known data cable/direct port, allow enumeration to settle, then run connection checks and compare the independent driver/transport findings. Do not infer a defective component from a node transition alone.

## Verification — November 3, 2026

- `dotnet build PhoneLink.slnx --no-restore -v quiet`: passed, zero warnings/errors.
- `dotnet test PhoneLink.slnx --no-build --no-restore -v quiet`: 406 passed. Five new history tests cover baseline/duplicates, redaction, bounded detached history, overflow, stop/late callbacks and unavailable-state handling. Two native Windows tests verify actual enumeration/disposal and pre-cancelled startup.
- Computer-use verification in the real Windows app: native baseline of four Apple USB nodes, no false initial arrival events, Stop displays cleared state, fresh restart again enumerates four nodes, and navigation away/back shows monitoring stopped without automatic resume.
- No physical unplug/replug was performed during this verification. Actual arrival/removal rendering, driver re-enumeration, multiple attached devices, native aborts, mid-start cancellation, lock/unlock and pending-operation privacy races remain runtime gates. Unit tests/source guards do not substitute for those checks. No successful phone repair is claimed.
- This feature is in source and the verified Debug build, not the previously signed development MSIX. No certificate trust/install or audio approval was inferred.

## Primary references

- [DeviceWatcher, Windows SDK 26100](https://learn.microsoft.com/en-us/uwp/api/windows.devices.enumeration.devicewatcher?view=winrt-26100): initial enumeration, continuing events, asynchronous Stop and callback unsubscription.
- [DeviceInformation.CreateWatcher, Windows SDK 26100](https://learn.microsoft.com/en-us/uwp/api/windows.devices.enumeration.deviceinformation.createwatcher?view=winrt-26100): AQS/property/device-kind overload used by this implementation.
