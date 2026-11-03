# Guided checks on the iPhone

In the companion, open **Health checks → Touch, display, sound and haptics**. These interactive tools help reproduce symptoms on the phone itself. They do not calibrate parts, run Apple's proprietary diagnostics, repair hardware, or certify a device healthy. They require a built and installed companion; Windows USB detection cannot run them remotely.

## What is actually observed

- **Touch:** a 6×8 grid fills only cells for which SwiftUI delivers a gesture location. Repeated events in one cell do not increase coverage. No lines are interpolated across skipped cells. Out-of-area, nonfinite and invalid-size inputs are ignored. Coverage applies only to this test area, not the entire screen, multi-touch, pressure, latency or calibration. Unfilled means untested, not failed. Accessibility gesture modes can affect delivery; the app does not request disabling them.
- **Display:** manually cycle black, white, red, green and blue. Nothing flashes automatically and brightness is unchanged. Controls/system regions remain outside the exposed test area. Each color's observation is self-reported, resets on advancing, and is not a saved panel measurement. Close at any time if uncomfortable.
- **Sound:** an explicit button requests one generated 0.4-second mono 440 Hz tone, with 15% PCM peak, 10 ms fades and player volume 0.5. This is not a guaranteed acoustic level: lower system volume first, especially with headphones. A separate Stop button and navigation/background cleanup stop playback. No microphone, recording, volume override, route override or audio-session category change. Current routing may send sound to headphones/Bluetooth and playback may interrupt other audio. Silent mode, volume, routing or interruptions can explain silence; no speaker fault is inferred.
- **Haptic:** an explicit button requests one SwiftUI impact. A request is not proof it was produced or felt. Device support, system decisions and user settings can suppress it. Nothing attempts to override those settings.

Sound/haptic observations remain disabled until their request is attempted. User choices are labeled as observations, never sensor measurements or automatic pass/fail. No permissions are requested. Touch locations are reduced immediately to occupied-cell indices; no raw path is retained. These states remain local memory, are not part of the health wire payload, are not written to disk or sent to Windows, and can be cleared explicitly. Leaving the active scene clears coverage/observations and dismisses display colors; navigation away stops audio. No automatic repair follows any result.

## Verification scope

Three native tests cover touch edges/deduplication/full synthetic coverage without interpolation, invalid geometry, and generated WAV format/length/peak/fades. These do not establish actual touch accuracy, display uniformity, acoustic output or haptic output. Physical-device operation, accessibility behavior, navigation/interruption races, and rendered UI inspection remain open until tested on an installed companion.

Native Xcode 26.3/iOS simulator [CI run 41605087525](https://github.com/MercyProductions/Phone-Link/actions/runs/41605087525) passed for source `8bb5f5a3517e98255037c8893eba78e96ed1e63a`, including all three new test cases. The log retains destination-selection and unused AppIntents-metadata warnings; this is not a warning-free native-build claim. No tone or haptic was executed by these helper tests. No physical iPhone installation, permission approval, security-setting change or hardware repair occurred.

## Primary API references

- [Apple DragGesture](https://developer.apple.com/documentation/swiftui/draggesture): location events in the specified coordinate space.
- [Apple sensory feedback](https://developer.apple.com/documentation/swiftui/view/sensoryfeedback(_:trigger:)): requests feedback when the explicit trigger changes; no hardware-success receipt.
- [Apple AVAudioPlayer data initializer](https://developer.apple.com/documentation/avfaudio/avaudioplayer/init(data:)): in-memory audio playback, not recording or acoustic measurement.
- [Apple audio-session behavior](https://developer.apple.com/documentation/avfaudio/avaudiosession): silent-mode and routing context must not be mistaken for hardware failure.
