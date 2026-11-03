# Windows Bluetooth phone-line investigation

Calls → **Inspect call transport**, or `dotnet run --project tools/PhoneLink.DeviceProbe --no-build -- --call-transport`, reads the dedicated Windows phone-line transport surface. It is separate from ANCS caller alerts, AMS media controls, A2DP media audio, PBAP history and the companion's confirmed dial request.

The probe enumerates the documented Bluetooth transport selector, then inspects at most 32 returned endpoints. It reads this app's `IsRegistered()` status and, when exposed, `AudioRoutingStatus`. Every field can remain unknown; a failed property read does not become false or ready. Enumeration denial/failure is distinct from a successful empty result. Counts, controlled status values and a timestamp are retained, never names, endpoint IDs, numbers, callers or history. Unexpected exceptions are not copied into reports. Cancellation is checked before/after enumeration and between inspections; WinRT synchronous property reads are not forcibly interrupted mid-call.

No request-access, registration/unregistration, connect/disconnect, call-store/history access, audio routing change or dial operation is performed. No manifest capability, driver, security policy, trust or existing call relationship was changed. `RegisterApp()` is a persistent per-user action, so it is not treated as a read-only experiment. Any later control experiment needs a separately reviewed capability/build and explicit device/action choice; this scan is not permission to place calls.

## Native evidence, November 3

Both standalone CLI and actual WinUI Calls-page inspection returned one Bluetooth phone-line endpoint, this app unregistered, audio routing reported `CanRouteToLocalDevice`, and zero access-denied observations. The computer-use skill was used to open Calls, scroll to the new control and run only the read-only inspection. The completed result was visibly rendered. No number was entered, no Send to iPhone action was invoked, and no access prompt or call occurred.

This is a concrete Windows route to investigate, not cryptographic proof of an iPhone endpoint or working carrier calling. It supersedes the earlier blanket public-route impossibility categorization, not the remaining deployment/control/audio gates. A reported routing capability does not prove a connected audio path, audible output, microphone return, answer/hang-up, incoming caller delivery or distribution eligibility.

Five application cases cover unavailable/denied versus empty enumeration and independent registration/routing observations. A native read-only integration case checks bounded results and pre-cancellation. Capability-denied runtime, forced mid-inspection failure, cancellation/navigation races, and packaged-versus-standalone behavior remain open. Register/connect, actual calling and audio were not tested. Existing certificate/install and playback questions remain unanswered.

Full verification: `dotnet build PhoneLink.slnx --no-restore -v quiet` passed with zero warnings/errors; `dotnet test PhoneLink.slnx --no-build --no-restore` passed all 377 cases. The CLI's added explicit Bluetooth-project reference is recorded in its lock file. `dotnet restore tools/PhoneLink.DeviceProbe/PhoneLink.DeviceProbe.csproj --locked-mode --no-dependencies --verbosity quiet` passed for that root project. A recursive locked restore without the existing shared projects' runtime target failed with NU1004; it is not claimed as passing. Incidental shared-lock changes from restore investigation were reverted, preserving the pre-existing shared locks and package versions.

## Primary references (Windows SDK 26100)

- [PhoneLineTransportDevice](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.calls.phonelinetransportdevice?view=winrt-26100) and [Bluetooth selector](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.calls.phonelinetransportdevice.getdeviceselector?view=winrt-26100).
- [IsRegistered](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.calls.phonelinetransportdevice.isregistered?view=winrt-26100): app registration observation.
- [AudioRoutingStatus](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.calls.phonelinetransportdevice.audioroutingstatus?view=winrt-26100): documented phoneCall capability requirement, not proof of permission from a readable result.
- [RegisterApp](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.calls.phonelinetransportdevice.registerapp?view=winrt-26100): registration persists per user until unregister/removal.
- [Restricted capability declarations](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations): phoneLineTransportManagement manages this surface; Store approval and platform eligibility cannot be inferred from sideloading or a successful query.
