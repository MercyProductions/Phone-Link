# Companion-owned runtime image metadata

The companion's Health tab now links to **Own runtime image and ASLR**. An explicit Capture action locates the loaded image containing a known companion marker through `dladdr`. Anchoring to resident app code avoids walking dyld's mutable image list; it does not enumerate all system/framework images or attempt to inspect unloadable plugins.

The bounded parser reads only the companion image's 64-bit Mach-O header/load-command metadata: at most 4,096 commands and 1 MiB of commands, one UUID and one file-backed `__TEXT` segment at file offset zero. It requires a matching command-table length, valid command sizes, required metadata, nonoverflowing text ranges and a marker inside the loaded text mapping. The runtime address minus preferred text address is reported as a signed hexadecimal slide; it is calculated from the observed mapping, not guessed from a file. An unsupported or inconsistent result fails without escalation.

The JSON has a fixed schema containing capture time, own-marker-image scope, code-image UUID, CPU type/subtype, preferred text address/size, loaded header address, slide, marker runtime address and marker image offset. No phone identifier, filesystem path, credential, raw memory bytes, symbols from other modules or phone content is exported. A code-image UUID identifies a build, not a physical phone.

The resolver requires matching UUID and exact CPU type/subtype and only maps unslid addresses inside the captured text range. Build matching is necessary but not cryptographic authenticity: a JSON document can be edited, and matching a UUID does not authenticate its origin or prove an address remains live. The capture applies only to that process run; restart/reload invalidates assumptions. No address is executed, dereferenced as an external target, or used to write memory. The Windows static report remains static; the CLI now offers a separate explicit snapshot-derived mapping below.

## Sharing and lifecycle

Capture runs only in the foreground on request. Review shows the entire JSON with a warning that live process addresses and slide are sensitive diagnostic information. Share invokes the normal iPhone share sheet only when the user chooses it; there is no automatic network or Windows transfer. The app does not write a snapshot file itself. The system share sheet/selected destination may retain a copy after the user shares. Clearing the view cannot revoke that copy. Leaving the view or active scene clears the in-app snapshot.

This is live metadata for the companion's own code image, not an arbitrary process-memory dump, kernel dump, system-wide offset database, decrypted image extraction, debugger attach or authenticated SSH endpoint. No task-for-pid, pairing credentials, developer image, private dyld interface, jailbreak or setting change is used. Physical-device capture and signed companion installation remain separate gates.

## Verification

Two native test cases target the actual loaded companion marker image and synthetic malformed headers/negative slides. They check marker mapping consistency, UUID/CPU mismatch refusal, exact JSON field allowlist, text bounds, command size/count consistency, missing UUID and address overflow. [Run 41607854988](https://github.com/MercyProductions/Phone-Link/actions/runs/41607854988) ended cancelled at the 25-minute job limit. Logs show app/test bundles compiled and validated, then no test results before cancellation; this does not prove the probe passed or establish why test execution stalled. The workflow now has a five-minute simulator boot preflight and a twelve-minute single-simulator test step. [Follow-up 41611855305](https://github.com/MercyProductions/Phone-Link/actions/runs/41611855305), source `46173a5`, was last observed queued. Native test success remains unproven. No owner phone data is included in CI.

Rendered iPhone UI, physical capture, pointer-authentication configurations, share-sheet/lifecycle behavior, cross-process access and actual companion-to-Windows snapshot roundtrip remain unverified. The code intentionally reports unavailable rather than stripping or guessing pointer-authentication bits.

## Windows CLI snapshot import

```powershell
dotnet run --project tools/PhoneLink.OffsetDumper -- 'C:\owned\matching-image.dylib' --runtime 'C:\owned\reviewed-runtime.json'
```

The optional UTF-8 JSON is limited to 8 KiB and the exact twelve-field schema. Duplicate/unknown/missing fields, invalid types, unsupported scope/version, inconsistent slide/marker arithmetic, overflow, ambiguous image matches, changed text layout and UUID/architecture mismatches fail without emitting a mapped report. The image's UUID, architecture, exact subtype, image base and file-backed `__TEXT` address/size must match. Only entries in that text range are mapped; constants and other ranges are not invented runtime addresses. Mapping is limited to 4,096 entries with explicit truncation, preserving pre-existing static truncation too.

Output contains the unchanged `StaticInspection` (`RuntimeAddressesAvailable=false`) plus `RuntimeTextMapping`. Every mapped value is labeled `SnapshotDerivedAddress`; `SnapshotOriginAuthenticated` and `LiveProcessVerified` are always false. This offline analysis does not expire historical captures or claim their addresses are currently live. The file UUID/layout comparison is not a content signature or proof that the JSON came from the companion. No input file is executed, modified or uploaded, and no process is read or attached.

Verification: 19 new .NET cases passed; full solution 396 passed; full Debug build zero warnings/errors. Actual CLI parsing of the project-owned native ARM64 fixture with `tests/Fixtures/runtime/synthetic-native-arm64.json` mapped its three entries to synthetic address `0x7000002e8`, retained static runtime availability false, and emitted both verification flags false. That JSON is intentionally synthetic, not captured from a phone or a live process, and does not prove Swift/Windows interoperability. Native companion verification remains the separately tracked pending run above.

## Windows app import

After choosing an owned image in **USB & binary tools**, select **Import reviewed runtime JSON**. The same mapper applies only to the selected architecture/image. The page displays at most 500 rows, the reported capture time, matched build, truncation status and explicit unverified-origin/live warnings. Import is bounded to 8 KiB with a twenty-second I/O deadline after the picker. A generation guard suppresses results invalidated by selection, navigation or privacy lock. Lock clears the displayed mapping and unlock rearms the button when otherwise eligible. No automatic import, live device read, upload, or runtime export was added. Existing Save JSON report serializes only the static inspection, not these rows.

Using the computer-use skill, the actual WinUI build selected the native ARM64 project fixture and the synthetic runtime JSON, rendered all three expected `0x7000002e8` rows and both false verification flags, then showed no runtime snapshot after leaving and returning. Full build zero warnings/errors and all 396 tests passed. Physical phone data was not imported. Lock/unlock, in-flight cancellation/selection races, mismatch/error UI, and static-save exclusion after an import were not independently exercised through UI; their source guards do not replace those runtime gates.

## Primary references

- [Apple dladdr manual](https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man3/dladdr.3.html): the returned image base is its loaded Mach-O header.
- [Apple dyld manual](https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man3/dyld.3.html): count-based image iteration is not thread-safe while images change; this probe uses a resident own-code anchor instead.
- [Apple Mach-O structures](https://github.com/apple-oss-distributions/xnu/blob/main/EXTERNAL_HEADERS/mach-o/loader.h): 64-bit headers, segment and UUID commands.
- [Apple symbolication and ASLR](https://developer.apple.com/videos/play/wwdc2021/10211/): build UUID and relocated addresses must be distinguished.
