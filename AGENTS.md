# Phone Link repository

Work from this repository's root, not an unrelated parent workspace. `PhoneLink.slnx` contains the Windows/application projects, six .NET test projects and two CLI tools. The Swift companion is a separate Xcode project.

## Source map

- `src/PhoneLink.Windows`: WinUI 3 UI/composition (`AppServices.cs`, `Pages`, `MainWindow.xaml.cs`). Targets Windows SDK 26100; runtime ID defaults to the host's Windows architecture. App lifecycle/privacy hooks are part of feature integration.
- `src/PhoneLink.Application`: platform-independent application policies, diagnostics and Mach-O inspection/mapping. `PhoneLink.Domain` contains domain types.
- `src/PhoneLink.Protocol`: strict wire/session/pairing formats. Changes crossing platforms also require checking Swift callers and matching protocol tests.
- `src/PhoneLink.DeviceCommunication`: authenticated local transport/discovery, experimental USB diagnostics and bounded forwarding. `PhoneLink.Integrations.AppleBluetooth` owns separate ANCS, AMS, audio and call-transport adapters.
- `src/PhoneLink.Security` and `src/PhoneLink.Persistence.Sqlite`: protected identity/storage and database integration. Do not treat raw USB presence or OS Bluetooth pairing as companion authentication.
- `tools/PhoneLink.DeviceProbe` and `tools/PhoneLink.OffsetDumper`: executable diagnostic entry points. The latter's default report is static; optional runtime mapping stays explicitly snapshot-derived and unverified live.
- `ios/PhoneLinkCompanion/PhoneLinkCompanion.xcodeproj`: Swift 6 / iOS 26 companion. New Swift files need the existing explicit PBX file/group/source entries; this project does not auto-discover every source file.

`bin`, `obj` and `artifacts` are generated/local outputs, not the default search scope. Existing versioned MSIX sets and `latest.json` do not establish that an installed app or package contains the current source.

## Verification commands

These commands have run successfully in this Windows workspace (not a claim of device-feature completion):

```powershell
dotnet build PhoneLink.slnx --no-restore -v quiet
dotnet test PhoneLink.slnx --no-build --no-restore
.\packaging\Test-PackageArtifacts.ps1
```

`global.json` pins .NET SDK 10.0.300 with latest-patch roll-forward. `Directory.Build.props` enables C# 14, recommended analyzers and warnings-as-errors. Test projects include native Windows integration checks; do not assume the whole suite is portable or hardware-independent. Builds can be locked by a running local debug app; identify the exact process/path before stopping any instance.

Shared dependency locks include Windows runtime targets. A recursive locked restore from a non-RID CLI graph can fail NU1004 even when that CLI's own lock is valid. Inspect target/lock differences; avoid unrelated lock regeneration or dependency changes as an incidental fix.

Native iOS verification is `.github/workflows/ios.yml` (Xcode 26.3, installed iOS 26 simulator). It explicitly checks simulator boot readiness and runs tests without simulator parallelization. Confirm the exact run/commit and terminal conclusion. Compilation, a booted simulator, and passing simulator tests are separate evidence; none proves signed physical-iPhone installation, permissions, pairing, audio or hardware behavior. Windows has no local Xcode/iOS SDK in this workspace.

## Detailed context

Use current implementation over historical phase plans. Load only relevant sections of `docs/accessory-repair-goal.md` for the temporary active-goal checkpoint; do not copy pending job handles or test counts into durable guidance. `docs/security-threat-model.md`, `docs/iphone-toolbox.md` and feature-specific documents record privacy boundaries and unverified device gates. A discovered Bluetooth service, callback-confirmed DNS-SD registration, USB forwarding, or matching runtime UUID is not end-to-end feature proof.
