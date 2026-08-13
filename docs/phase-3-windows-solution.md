# Phase 3: Windows solution

Phase 3 establishes the compilable Windows foundation. It deliberately contains no simulated phone data and no feature that claims an iPhone capability before the companion and transport exist.

## Toolchain

- .NET SDK 10.0.300, pinned by `global.json`
- C# 14 with nullable reference types and recommended analyzers
- WinUI 3 / Windows App SDK 2.3.1
- Windows 11 SDK baseline 10.0.26100
- Single-project MSIX packaging
- xUnit test projects

The solution targets .NET 10 because it is the current LTS release and satisfies the requirement for .NET 8 or newer.

## Projects

| Project | Responsibility |
| --- | --- |
| `PhoneLink.Domain` | Devices, capability identifiers, and synchronization entities |
| `PhoneLink.Protocol` | Versioned envelopes, strict JSON parsing, and protocol validation |
| `PhoneLink.Application` | Use-case services and infrastructure ports |
| `PhoneLink.Security` | Cryptographic primitives and protected-secret implementations |
| `PhoneLink.Persistence.Sqlite` | Local database implementation and migrations |
| `PhoneLink.DeviceCommunication` | Transport state machine and Wi-Fi/BLE transports |
| `PhoneLink.Integrations.AppleBluetooth` | Capability probes for optional Apple Bluetooth profiles |
| `PhoneLink.Windows` | WinUI 3 composition root and desktop UI |

The dependency direction is inward: UI and infrastructure depend on application abstractions and domain objects; the domain does not depend on Windows or iOS APIs.

## Desktop shell

The packaged desktop app has working navigation for Messages, Contacts, Calls, Notifications, Connected Devices, Settings, and About. The Messages page already uses the intended three-region desktop layout. Controls that need a paired phone remain disabled until their use cases are connected in later phases.

The package manifest requests only capabilities needed by the documented design: local/client networking, private-network server access, Bluetooth, and full-trust desktop execution. It does not request Windows AI or unrelated capabilities.

## Build and test

From the repository root in PowerShell:

```powershell
dotnet restore PhoneLink.slnx
dotnet build PhoneLink.slnx -c Debug --no-restore
dotnet test PhoneLink.slnx -c Debug --no-build --no-restore
```

To run the packaged desktop app from a development machine:

```powershell
dotnet run --project src\PhoneLink.Windows\PhoneLink.Windows.csproj -c Debug
```

The first run can prompt Windows to allow private-network access. No inbound port is opened until the local pairing listener is introduced.

## Verification result

On 2026-08-12 the complete solution restored and built with zero warnings and zero errors. All 12 initial tests passed. These tests cover device trust behavior, strict protocol parsing and limits, application ordering, storage option validation, and allowed transport transitions.

The iOS companion cannot be compiled or signed on Windows. Phase 4 therefore includes a native Xcode project and Mac build commands, while structural and cross-platform protocol checks remain part of this repository.
