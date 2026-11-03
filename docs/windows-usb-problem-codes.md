# Windows USB problem-code diagnostics

The Troubleshooter now requests the numeric `DEVPKEY_Device_ProblemCode` for present Apple-vendor USB device nodes. It aggregates only codes and node counts, not names, serial numbers or instance IDs. Existing UI finding bindings display the results; the explicitly copied connection report includes the same generic evidence and guidance.

The property key is `{4340a6c5-93fa-4706-972c-7b648008a5a7} 3`, verified against Windows SDK 10.0.26100 `devpkey.h`. Windows DeviceInformation supports this [GUID/PID request syntax](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/device-information-properties). The SDK declares UInt32; the [property documentation](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-problemcode) describes Int32. The parser accepts UInt32 and nonnegative Int32 only. Missing, negative, string and Boolean values remain unavailable, never healthy zero.

The scan adds tailored guidance for start failure (10), restart required (14), disabled device (22), missing drivers (28), and driver-reported failure (43). Other nonzero codes retain their numeric evidence and link to the [official Microsoft reference](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/device-manager-error-messages), without guessing a cause. A nonzero code makes the driver finding attention-worthy even if the separately read Boolean flag was false. Coverage is reported separately; missing numeric evidence does not erase existing Boolean evidence.

No code automatically enables a device, restarts Windows, installs drivers, changes services, resets trust or modifies the phone. Code 43 alone cannot identify a faulty cable, port, phone component or driver. Apple-vendor node counts are not physical-phone counts or proof of iPhone identity, iOS Trust, charging quality or hardware health.

## Verification

- `dotnet build PhoneLink.slnx --no-restore -v quiet`: passed with zero warnings/errors.
- `dotnet test PhoneLink.slnx --no-restore`: 371 passed, including 16 added cases for code mapping, numeric typing, unavailable coverage and conflicting Boolean/code evidence.
- Live `dotnet run --project tools/PhoneLink.DeviceProbe --no-build -- --connection-check`: four present Apple USB nodes, no Boolean problems, numeric codes available for all four, no nonzero-code finding. Two eligible local addresses. No identifiers or phone content printed.
- The CLI host check intentionally does not initialize the app's pairing service, read companion trust, or infer package identity. Those fields remain unknown there; the Windows page continues to supply them through its existing paths.
- Physical failing-node states and new fault-row rendering in the Windows UI remain unverified. No device was disabled or driver broken to manufacture a test. Synthetic cases prove guidance selection, not that any suggested action fixes a particular phone.

This expands diagnosis and repair selection, not automatic phone hardware repair. App-owned discovery recovery remains a separate bounded action.
