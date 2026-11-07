# Local iPhone crash and Jetsam report analysis

The Troubleshooter can analyze an explicitly selected `.ips`/`.json` file containing Apple's type-309 JSON crash format or a type-298 Jetsam report with a process table. No phone connection or companion is needed once the owner exports the file to Windows. It is a first diagnostic tool, not a phone repair or a substitute for full symbolicated crash analysis.

## Use and privacy

1. Export a report you choose from iPhone Settings → Privacy & Security → Analytics & Improvements → Analytics Data and transfer it to Windows yourself. Menu availability may vary; no bulk log collection or USB log extraction is implemented.
2. In Phone Link → Troubleshooter, choose **Analyze selected .ips report** and select that file. Both supported formats are bounded to 4 MiB and JSON nesting depth 64. Panic, stackshot and translated text remain unsupported.
3. Review the category, redacted evidence, limitations and next steps. **Copy redacted analysis** is separate from the connection-scan copy button and copies only the displayed summary.

Use **Compare 2–12 reports** to count repeated recognized categories in one selection. Each file has the same 4 MiB limit. Byte-identical files count only once; unreadable, unsupported and unclassified inputs do not count toward a category. The summary does not establish unique incidents, a shared device or app, or a common cause. Neither filenames nor content hashes are displayed.

The analyzer does not upload, modify or persist the source. Unknown strings are not echoed; app names, paths, timestamps, report/device identifiers, log messages and stack traces are not in the result. Raw JSON parser errors are not displayed. Input bytes exist transiently in process memory; secure memory erasure and exclusion from operating-system crash dumps are not claimed. The clipboard is managed by Windows and is not cleared automatically; review before sharing. Original reports may contain private information and must be handled separately.

The included [synthetic fixture](fixtures/synthetic-app-crash.ips) contains no actual device data. Its expected category is **Invalid memory access signature**, and its artificial process/incident labels must not appear in the result.

## Interpretation and bounds

Known exception families produce narrowly worded signatures, not a root-cause verdict. A watchdog signature requires numeric `0x8badf00d` with a recognized process-management namespace; a matching arbitrary string or unrelated namespace is insufficient. Nonfatal, simulated and ambiguous flags prevent a claim of an actual crash. Duplicate JSON properties, invalid UTF-8/JSON, excess nesting, extra root values, missing body, unsupported metadata and oversized input are rejected. Unknown fields and signatures remain unrecognized rather than guessed.

For Jetsam, a bounded table of process objects is required. An allowlisted reason is reported only if exactly one entry has a reason field. The reported resident-memory calculation uses `memoryStatus.pageSize × rpages` for that same marked entry, with bounds and overflow checks. Missing or ambiguous data does not produce a guessed reason or memory measurement; the largest process is not assumed to have been terminated or to have caused the event. No process names or other original log identifiers are included. This is historical file evidence, not current memory usage or a diagnosis of the owner’s phone.

Memory-access, resource, trap, arithmetic and abort reports do not prove defective RAM, battery, storage, processor or motherboard. The app does not run commands, reset trust, restore firmware or replace drivers based on a report. Verified incident grouping, time/symptom correlation, symbolication, real owner-report validation and justified repair actions remain open.

## Verification

Added 37 synthetic parser cases covering known/unknown signatures, watchdog namespace guards, nonfatal flags, privacy, invalid UTF-8/duplicate/trailing input, cancellation, exact/over-limit sizes, short reads, excessive depth, stream ownership and the embedded documentation fixture. Full solution tests: 299 passed. Build: zero warnings/errors after closing only the verified own Debug test process; the initial attempt hit that running process's file locks and two test-string syntax errors, both resolved and rerun.

Windows runtime, updated November 3: migrated import to the Windows App SDK desktop picker bound to the app's WindowId. Selected the synthetic fixture through the real dialog and verified the displayed category, signal and redaction. The explicit copy action succeeded; a clipboard check found the expected category/privacy notice and no synthetic private marker (771 characters). Picker element-index targeting still failed in the helper, but screenshot-based dialog actions worked. This closes successful synthetic import/display/copy gates, not validation of actual owner reports, unload during import or packaged behavior. No real owner crash report has been imported. The existing signed MSIX predates this crash-analysis addition and remains uninstalled; approval remains limited to that exact older build/certificate. Full solution regression checks after picker migration: 317 tests passed, build zero warnings/errors.

## Primary references

- [Apple JSON crash-report structure](https://developer.apple.com/documentation/xcode/interpreting-the-json-format-of-a-crash-report): two objects, type-309 metadata, documented exception/termination fields and decimal numeric codes.
- [Apple exception taxonomy](https://developer.apple.com/documentation/xcode/understanding-the-exception-types-in-a-crash-report): signature families, not hardware diagnoses.
- [Apple watchdog terminations](https://developer.apple.com/documentation/xcode/addressing-watchdog-terminations): responsiveness deadline and `0x8badf00d`.
- [Apple detailed analysis workflow](https://developer.apple.com/documentation/xcode/analyzing-a-crash-report): the full original report and context remain important for developer diagnosis.
- [Apple Jetsam analysis](https://developer.apple.com/documentation/xcode/identifying-high-memory-use-with-jetsam-event-reports): reason attribution and the documented page-to-byte calculation.
