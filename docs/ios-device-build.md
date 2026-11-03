# Physical-iPhone build and installation gates

The companion targets iOS 26 or later. Simulator tests do not establish that the production ARM64 device target builds, that an IPA contains the right executable/resources, or that a signed app installs and runs on a phone.

## Unsigned device build

The `device_archive` job in `.github/workflows/ios.yml` uses Xcode 26.3, Release configuration, `generic/platform=iOS`, and explicit `CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO`. It does not use `-allowProvisioningUpdates`, signing secrets, Apple credentials, a device identifier or a provisioning profile. It does not connect to a phone or install anything.

After archiving, Apple `lipo`, `vtool` and `dwarfdump` inspect architecture, build platform and UUID. The job stages the archive's app in standard `Payload/PhoneLinkCompanion.app` structure with `ditto`, preserving normal bundle data while omitting resource forks. It uploads `PhoneLinkCompanion-unsigned.ipa` and `device-build.json` as `ios-unsigned-device`, retained seven days. **Unsigned means not ready to install.** A linker-generated ad-hoc code signature, if present, is not an Apple development/distribution signing identity or provisioning profile.

`ios/scripts/verify_device_archive.py` requires the expected bundle identity and executable, iPhoneOS plist/platform metadata, minimum OS, camera/Contacts/local-network usage descriptions, pairing URL/Bonjour handlers, compiled assets and the existing no-developer-collection/tracking privacy declarations. Its bounded Mach-O parser checks ARM64 executable type, UUID and LC_BUILD_VERSION device platform; ARM64 simulator code is explicitly rejected. It checks every packaged file against the archive by relative inventory, size and SHA-256, rejecting unsafe paths, symlinks, profiles and test bundles. The report identifies the source commit, build metadata, file count and hashes, and explicitly leaves signing and physical installation unverified. These are artifact consistency checks, not an Apple distribution validation or a privacy-compliance certification.

Local validator tests use intentionally synthetic, non-runnable fixtures. They cover matching hashes/false verification flags, ARM64 simulator versus device, x64 rejection, mismatched deployment metadata, missing permission reason, unexpected profile, truncated commands, unsafe/foreign ZIP paths and changed executable/assets. All 13 passed on Windows and in the native job. These fixtures are not a physical-device execution test.

## Native build and Windows cross-check — November 3

[Run 41622905442](https://github.com/MercyProductions/Phone-Link/actions/runs/41622905442), source `0c9cae6d1fb6c4e666f97383b02dfae584ae4d21`, is terminal success for both jobs. Release/device archive, Apple tool inspections, all-file package matching and artifact upload passed. Simulator boot/test also passed six XCTest plus twelve Swift Testing cases (18 total). The device job took 69 seconds; the simulator job took about seven minutes. AppIntents metadata and action-runtime deprecation warnings remain; this is not a warning-free native-build claim.

The device artifact contains seven files; its executable is 1,049,480 bytes, ARM64 subtype zero, UUID `bb1b7c0b-8a56-39fa-9caa-9239cad8588c`, platform iOS, minimum 26.0, SDK 26.2. IPA SHA-256: `e7413fdbb478459e1dd3bb31f02da552cb9cfe824ecb9ea7e67bcd9e12199647`. Executable SHA-256: `0f5664fbf4a51deb93db6ec6fb4b7c6f80d79de9e72b5c0bc6ae4b62d3d549b6`. These identify build artifacts, not an owner phone.

Downloaded the exact artifact on Windows, verified both hashes, and extracted only its executable into a new local artifact directory without executing it. This exposed an offset-inspector incompatibility with the legitimate PIE `__mh_execute_header` symbol, which is section-tagged but sits before the first code section. The focused parser correction recognizes only a properly bounded, unique header mapping and preserves ordinary symbol bounds. Actual CLI inspection now matches native hash/UUID/type, parses 4,043 entries and labels the header as `image-header` at file offset zero, with runtime addresses still false. IPA and extracted input hashes remain unchanged. Nine regression cases bring the .NET solution to 440 passing tests, with full build zero warnings/errors. [Parser scope and references](usb-tunnels-and-static-offsets.md).

Signing, signed bundle/entitlement validation, installed launch, physical permissions/pairing and phone behavior remain unverified. No account or device state changed. The new device image's static CLI inspection does not prove live offset resolution, and its Windows UI import has not been separately exercised.

## Owner-controlled signing and installation

Apple requires signing and provisioning before a device app can launch. An unsigned IPA is not something Apple Devices can simply install, nor an alternative to those requirements. No signing bypass, account sharing or security-setting change is part of this build job.

- With access to a Mac/Xcode, open the project, choose your own development team and physical device, resolve bundle-identifier availability in your account and use Xcode's normal signing/run workflow. Keep credentials and signing keys in your own tools, never chat or this public repository.
- TestFlight/registered-device distribution is another owner-managed path with the corresponding Apple account/program and signing requirements. No enrollment, fees, device registration or upload is initiated here.
- If only Windows is available, the unsigned artifact removes the local compilation requirement but does not establish a supported signing/install route. A specific route and its account/security implications still need owner choice and current verification before any software installation or credential use. No third-party signing service has been selected or trusted.

After signed installation, verify launch, Keychain identity persistence, foreground QR scan/review, six-word comparison, local-network permission, authenticated reconnect and real health delivery. Then test Contacts/Photos and guided checks separately with user approval. Physical Camera-link recognition, accessory call/audio behavior and hardware repair remain separate requirements; an installed companion alone does not prove them.

## Primary references

- [Apple: create an archive](https://help.apple.com/xcode/mac/current/en.lproj/devf37a1db04.html): choose an iOS device destination for an iPhone archive.
- [Apple: run an app on a device](https://help.apple.com/xcode/mac/current/en.lproj/dev5a825a1ca.html): device signing/provisioning and deployment-target requirements.
- [Apple: distribute to registered devices](https://developer.apple.com/documentation/xcode/distributing-your-app-to-registered-devices): owner-managed distribution/signing workflow.
