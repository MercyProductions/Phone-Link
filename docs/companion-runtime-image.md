# Companion-owned runtime image metadata

The companion's Health tab now links to **Own runtime image and ASLR**. An explicit Capture action locates the loaded image containing a known companion marker through `dladdr`. Anchoring to resident app code avoids walking dyld's mutable image list; it does not enumerate all system/framework images or attempt to inspect unloadable plugins.

The bounded parser reads only the companion image's 64-bit Mach-O header/load-command metadata: at most 4,096 commands and 1 MiB of commands, one UUID and one file-backed `__TEXT` segment at file offset zero. It requires a matching command-table length, valid command sizes, required metadata, nonoverflowing text ranges and a marker inside the loaded text mapping. The runtime address minus preferred text address is reported as a signed hexadecimal slide; it is calculated from the observed mapping, not guessed from a file. An unsupported or inconsistent result fails without escalation.

The JSON has a fixed schema containing capture time, own-marker-image scope, code-image UUID, CPU type/subtype, preferred text address/size, loaded header address, slide, marker runtime address and marker image offset. No phone identifier, filesystem path, credential, raw memory bytes, symbols from other modules or phone content is exported. A code-image UUID identifies a build, not a physical phone.

The resolver requires matching UUID and exact CPU type/subtype and only maps unslid addresses inside the captured text range. Build matching is necessary but not cryptographic authenticity: a JSON document can be edited, and matching a UUID does not authenticate its origin or prove an address remains live. The capture applies only to that process run; restart/reload invalidates assumptions. No address is executed, dereferenced as an external target, or used to write memory. The current Windows static report remains static and does not automatically consume this JSON.

## Sharing and lifecycle

Capture runs only in the foreground on request. Review shows the entire JSON with a warning that live process addresses and slide are sensitive diagnostic information. Share invokes the normal iPhone share sheet only when the user chooses it; there is no automatic network or Windows transfer. The app does not write a snapshot file itself. The system share sheet/selected destination may retain a copy after the user shares. Clearing the view cannot revoke that copy. Leaving the view or active scene clears the in-app snapshot.

This is live metadata for the companion's own code image, not an arbitrary process-memory dump, kernel dump, system-wide offset database, decrypted image extraction, debugger attach or authenticated SSH endpoint. No task-for-pid, pairing credentials, developer image, private dyld interface, jailbreak or setting change is used. Physical-device capture and signed companion installation remain separate gates.

## Verification

Two native test cases exercise the actual loaded companion marker image and synthetic malformed headers/negative slides. They check marker mapping consistency, UUID/CPU mismatch refusal, exact JSON field allowlist, text bounds, command size/count consistency, missing UUID and address overflow. Native CI verification is recorded after the run completes. No owner phone data is included in CI.

Rendered UI, physical iPhone capture, pointer-authentication configurations, share-sheet/lifecycle behavior, cross-process access and Windows import/integration remain unverified. The code intentionally reports unavailable rather than stripping or guessing pointer-authentication bits.

## Primary references

- [Apple dladdr manual](https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man3/dladdr.3.html): the returned image base is its loaded Mach-O header.
- [Apple dyld manual](https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man3/dyld.3.html): count-based image iteration is not thread-safe while images change; this probe uses a resident own-code anchor instead.
- [Apple Mach-O structures](https://github.com/apple-oss-distributions/xnu/blob/main/EXTERNAL_HEADERS/mach-o/loader.h): 64-bit headers, segment and UUID commands.
- [Apple symbolication and ASLR](https://developer.apple.com/videos/play/wwdc2021/10211/): build UUID and relocated addresses must be distinguished.
