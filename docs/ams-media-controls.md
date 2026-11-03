# iPhone media controls

The Windows **iPhone media** page implements Apple Media Service (AMS) remote commands and timestamped player/queue/track observations. It is not audio streaming, call audio, Siri, or a claim of AirPods/Watch parity. Actual media control remains unverified because the current Windows environment denies characteristic access.

## Source and implemented scope

The [AMS specification](https://developer.apple.com/library/archive/documentation/CoreBluetooth/Reference/AppleMediaService_Reference/Specification/Specification.html) defines optional Remote Command and Entity Update characteristics. Command support is reported by the media source; an accepted write does not guarantee a player effect. The [reference appendix](https://developer.apple.com/library/archive/documentation/CoreBluetooth/Reference/AppleMediaService_Reference/Appendix/Appendix.html) supplies command IDs and entity formats.

The implementation covers all fourteen defined commands: play, pause, toggle, next/previous track, volume up/down, repeat/shuffle cycling, forward/backward skip, like/dislike, and bookmark. Controls are unavailable until a supported-command notification is received; unknown IDs are never executed. Metadata subscriptions cover the three defined entities and eleven attributes. Characteristics are optional: usable channels can operate independently, and setup failures are reported without inventing missing values. Extended Entity Attribute reads are not yet implemented; truncation remains visible.

## Workflow and boundaries

1. Select **Scan paired devices**. Discovery probes existing paired BLE devices, reports unavailable probes separately, and lists only AMS advertisers. A name is not authenticated ownership or proof of characteristic access.
2. Explicitly select your own device. There is no automatic first-device selection or fallback to another phone. Startup rechecks the selected Windows device ID against the paired inventory, requests service access, then checks actual characteristics.
3. Setup subscribes for command support and observations. It sends no playback/volume/preference command. Any permission decision remains the user's; the app does not approve dialogs, change settings, bypass access policy, read pairing credentials or install software.
4. Choose an advertised command and review its confirmation. Cancel is the default. Each confirmation creates a session-bound, single-use request. A new, explicitly confirmed request may repeat a command, but the same request cannot be submitted twice.
5. Stop/cancel, navigate away, or lock Phone Link to cancel pending work, close confirmation, clear observations/command knowledge, and release owned WinRT objects. Disconnect or a service change permanently ends that session object. A fresh session is required afterward.

Commands are serialized and recheck session authorization, current availability, and observed context after waiting. Changes to player name, track fields, queue index or the supported command set invalidate a pending confirmation; routine elapsed/volume updates do not. This is a best-effort check of observed context, not an atomic identity guarantee for the phone's active player.

Write-with-response and write-without-response characteristics are distinguished. Results are `NotSent`, `WriteAccepted`, `SubmittedUnacknowledged`, `DeviceRejected` with an available protocol error, or `OutcomeUnknown`. An ambiguous in-flight write is never automatically retried. An already submitted command cannot be recalled. The UI never optimistically changes playback/volume to match a requested command.

Input buffers and text are bounded, UTF-8 is validated, and truncated/unknown-flag numeric observations are not interpreted as complete values. Non-finite/out-of-range volume and malformed playback data remain unvalidated. Fields arrive independently with individual received times; they are not an atomic now-playing snapshot. No playback clock, progress, battery health or command effect is invented. Metadata remains in memory, is not exported/logged/persisted, and raw incoming byte copies are cleared after parsing. Cleanup does not send a delayed CCCD-disable write or forcibly disconnect other Windows clients; system-level link teardown is not claimed.

## Evidence, November 3, 2026

- Debug solution build: zero warnings/errors. All 259 .NET tests passed, including 51 new AMS cases.
- Tests cover all defined wire IDs, subscriptions, bounded supported lists, UTF-8/entity parsing, truncation, numeric validation, context invalidation, cross-session rejection, ended/disposed lifetimes, queued and in-flight cancellation, concurrent duplicate requests, distinct manual requests, and acknowledged/unacknowledged/rejected/unknown results.
- Live Windows UI verified navigation, readable layout, disabled controls before discovery, explicit selection, scan completion, and startup failure handling.
- Scan found one paired AMS advertiser and one unavailable probe. Selecting the owner's device and starting setup returned **service access Allowed, characteristic access AccessDenied**. The device's personal name/ID is excluded from these public notes.
- No playback, volume, skip, preference, or bookmark command was sent. No permission prompt was answered and no phone setting was changed. Metadata registration was not reached because characteristic access failed.
- Successful native setup, command-list delivery, metadata callbacks, populated command/observation UI, confirmation/cancel on a live session, actual player effects, and physical disconnect/lock/navigation races remain unverified. Unit tests and compilation do not prove these gates.

Next priority: investigate the common ANCS/AMS characteristic-access denial, compare a current packaged build's supported access behavior, and check the user's existing iPhone Bluetooth sharing state. Do not treat this denial as a hardware fault, assume a particular cause, weaken OS policy, or reset trust as a generic repair. The complete [accessory/repair/tunnel/dumper goal](accessory-repair-goal.md) remains active.
