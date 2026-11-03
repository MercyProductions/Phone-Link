# Local iPhone crash-report analysis

The Troubleshooter can analyze an explicitly selected `.ips`/`.json` file containing Apple's documented type-309 JSON crash format. No phone connection or companion is needed once the owner exports the file to Windows. It is a first diagnostic tool, not a phone repair or a substitute for full symbolicated crash analysis.

## Use and privacy

1. Export a report you choose from iPhone Settings → Privacy & Security → Analytics & Improvements → Analytics Data and transfer it to Windows yourself. Menu availability may vary; no bulk log collection or USB log extraction is implemented.
2. In Phone Link → Troubleshooter, choose **Analyze selected .ips report** and select that file. Only documented type-309 JSON is supported, up to 4 MiB and JSON nesting depth 64. Panic, Jetsam, stackshot and translated text support remains future work, not an excluded goal.
3. Review the category, allowlisted exception/signal/namespace fields, numeric termination code, limitations and next steps. **Copy redacted analysis** is separate from the connection-scan copy button and copies only the displayed summary.

The analyzer does not upload, modify or persist the source. Unknown strings are not echoed; app names, paths, timestamps, report/device identifiers, log messages and stack traces are not in the result. Raw JSON parser errors are not displayed. Input bytes exist transiently in process memory; secure memory erasure and exclusion from operating-system crash dumps are not claimed. The clipboard is managed by Windows and is not cleared automatically; review before sharing. Original reports may contain private information and must be handled separately.

The included [synthetic fixture](fixtures/synthetic-app-crash.ips) contains no actual device data. Its expected category is **Invalid memory access signature**, and its artificial process/incident labels must not appear in the result.

## Interpretation and bounds

Known exception families produce narrowly worded signatures, not a root-cause verdict. A watchdog signature requires numeric `0x8badf00d` with a recognized process-management namespace; a matching arbitrary string or unrelated namespace is insufficient. Nonfatal, simulated and ambiguous flags prevent a claim of an actual crash. Duplicate JSON properties, invalid UTF-8/JSON, excess nesting, extra root values, missing body, unsupported metadata and oversized input are rejected. Unknown fields and signatures remain unrecognized rather than guessed.

Memory-access, resource, trap, arithmetic and abort reports do not prove defective RAM, battery, storage, processor or motherboard. The app does not run commands, reset trust, restore firmware or replace drivers based on a report. Repeated-incident grouping, time/symptom correlation, symbolication, real owner-report validation and justified repair actions remain open.

## Verification

Added 37 synthetic parser cases covering known/unknown signatures, watchdog namespace guards, nonfatal flags, privacy, invalid UTF-8/duplicate/trailing input, cancellation, exact/over-limit sizes, short reads, excessive depth, stream ownership and the embedded documentation fixture. Full solution tests: 299 passed. Build: zero warnings/errors after closing only the verified own Debug test process; the initial attempt hit that running process's file locks and two test-string syntax errors, both resolved and rerun.

Windows runtime: the new section and initially disabled copy button render correctly; the report picker opens with `.ips`/`.json` filters. The computer-use helper does not expose the picker as a selectable target window, so selecting a file, displaying a successful analysis, copying it and unloading during import remain manual UI gates. No real owner crash report has been imported. The existing signed MSIX predates this crash-analysis addition and is still uninstalled; its approval remains limited to that exact older build/certificate.

## Primary references

- [Apple JSON crash-report structure](https://developer.apple.com/documentation/xcode/interpreting-the-json-format-of-a-crash-report): two objects, type-309 metadata, documented exception/termination fields and decimal numeric codes.
- [Apple exception taxonomy](https://developer.apple.com/documentation/xcode/understanding-the-exception-types-in-a-crash-report): signature families, not hardware diagnoses.
- [Apple watchdog terminations](https://developer.apple.com/documentation/xcode/addressing-watchdog-terminations): responsiveness deadline and `0x8badf00d`.
- [Apple detailed analysis workflow](https://developer.apple.com/documentation/xcode/analyzing-a-crash-report): the full original report and context remain important for developer diagnosis.
