# Phone media audio to PC

The **Phone audio to PC** page uses Windows `AudioPlaybackConnection` to receive Bluetooth media audio through the PC's system output. This is separate from AMS commands/metadata, ANCS notifications and cellular HFP calls. It does not implement a microphone return path, carrier-call audio, AirPods pairing, spatial audio or automatic Apple ecosystem switching. Bluetooth source support is required; USB detection and companion trust do not enable this transport.

## Operation

1. Pair your owned phone with Windows yourself if it is not already paired. The app does not pair devices or approve permission prompts.
2. Open the page and choose **Find audio sources**. The documented Windows selector is queried with a 15-second deadline; no receiver is created, enabled or opened during discovery. Names are labels, not authenticated identity. Device IDs remain local and are not copied to a report.
3. Explicitly select your phone and choose **Receive selected phone audio**. Review the confirmation, which defaults to Cancel. Only approval creates a connection, calls `StartAsync`, then `OpenAsync`, with a 30-second deadline. Existing phone media may immediately become audible through PC speakers/headphones. The app does not record, upload or change volume.
4. Verify sound yourself. `Success`/`Opened` is a Windows connection result, not proof of samples, sound quality, latency or audibility. Stop releases only this app's connection; it does not pause the phone and cannot release another app's references.

The session is single-attempt: failure, cancellation and changed page/selection/lock context release it without retry. Stop remains available while setup is pending. Navigation, hide-to-tray, app lock and window close request cleanup; Windows session lock/disconnect/logoff and suspend notifications also request cleanup through the UI dispatcher, independently of optional Windows Hello locking. Closed/unknown native state is polled once a second after setup and releases the receiver. Cleanup is best effort, not a guarantee of instant action during an OS transition; an in-progress or failed release stays unknown. A new connection requires another explicit user intent and confirmation. No automatic reconnect is implemented.

## Verification and remaining gates

- Native integration compiles against this project's Windows SDK 10.0.26100 target without adding restricted capabilities or changing the package manifest.
- Eighteen new session tests cover enable/open ordering, all modeled failure results, no retry, cancelled/stale context before/during setup, explicit Stop with late native success, single-use attempts, disposal ownership, state errors, failed cleanup and cleanup still in progress. Full solution: 317 tests passed; build zero warnings/errors. An initial build error called a MainWindow member through the base Window reference; corrected with a checked type pattern and rebuilt.
- Live standalone Windows UI: page renders, initial Open/Stop are disabled, discovery found one source, explicit selection enabled Open, confirmation rendered with Cancel as default, and Cancel returned to the page with no receiver enabled. Source names/IDs are deliberately excluded from this public checkpoint. Computer-use skill used for this verification.
- Native `StartAsync`/`OpenAsync` and actual audio playback were **not** executed. Owner permission to make current phone audio audible was requested and is pending. Real sound, failures, disconnect/reconnect, Stop during actual setup/playback, navigation/hide/lock/suspend cleanup, multiple apps and packaged runtime behavior remain physical-device gates. Test doubles do not prove those effects.
- The existing signed MSIX predates both crash analysis and this page; no new certificate was trusted or package installed. Cellular-call audio remains a distinct unresolved requirement, not fulfilled by A2DP.

## Primary references

- [Microsoft remote Bluetooth audio guide](https://learn.microsoft.com/en-us/windows/apps/develop/media-playback/enable-remote-audio-playback): selector, enabling/opening and releasing app-held A2DP sink connections.
- [AudioPlaybackConnection, SDK 26100](https://learn.microsoft.com/en-us/uwp/api/windows.media.audio.audioplaybackconnection?view=winrt-26100).
- [Open outcomes](https://learn.microsoft.com/en-us/uwp/api/windows.media.audio.audioplaybackconnectionopenresultstatus?view=winrt-26100) and [connection states](https://learn.microsoft.com/en-us/uwp/api/windows.media.audio.audioplaybackconnectionstate?view=winrt-26100).
- [PhoneLineTransportDevice](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.calls.phonelinetransportdevice?view=winrt-26100): a different calling surface with its own capability requirements; no restricted phone capability was added in this milestone.
