# Physical-iPhone build and installation gates

The companion targets iOS 26 or later. Simulator tests do not establish that the production ARM64 device target builds, that an IPA contains the right executable/resources, or that a signed app installs and runs on a phone.

## Unsigned device build

The `device_archive` job in `.github/workflows/ios.yml` uses Xcode 26.3, Release configuration, `generic/platform=iOS`, and explicit `CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO`. It does not use `-allowProvisioningUpdates`, signing secrets, Apple credentials, a device identifier or a provisioning profile. It does not connect to a phone or install anything.

After archiving, Apple `lipo`, `vtool` and `dwarfdump` inspect architecture, build platform and UUID. The job stages the archive's app in standard `Payload/PhoneLinkCompanion.app` structure with `ditto`, preserving normal bundle data while omitting resource forks. It uploads `PhoneLinkCompanion-unsigned.ipa` and `device-build.json` as `ios-unsigned-device`, retained seven days. **Unsigned means not ready to install.** A linker-generated ad-hoc code signature, if present, is not an Apple development/distribution signing identity or provisioning profile.

`ios/scripts/verify_device_archive.py` requires the expected bundle identity and executable, iPhoneOS plist/platform metadata, minimum OS, camera/Contacts/local-network usage descriptions, pairing URL/Bonjour handlers, compiled assets and the existing no-developer-collection/tracking privacy declarations. Its bounded Mach-O parser checks ARM64 executable type, UUID and LC_BUILD_VERSION device platform; ARM64 simulator code is explicitly rejected. It checks every packaged file against the archive by relative inventory, size and SHA-256, rejecting unsafe paths, symlinks, profiles and test bundles. The report identifies the source commit, build metadata, file count and hashes, and explicitly leaves signing and physical installation unverified. These are artifact consistency checks, not an Apple distribution validation or a privacy-compliance certification.

Local validator tests use intentionally synthetic, non-runnable fixtures. They cover matching hashes/false verification flags, ARM64 simulator versus device, x64 rejection, mismatched deployment metadata, missing permission reason, unexpected profile, truncated commands, unsafe/foreign ZIP paths and changed executable/assets. The source has 13 passing validator tests on Windows. A native archive/package run is pending; do not treat those fixture tests as a completed physical-target build.

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
