using System.Globalization;

namespace PhoneLink.Application.Features.Diagnostics;

public enum DiagnosticOutcome { Passed, Attention, Unknown, Guidance }

public sealed class DiagnosticFinding(string id, string title, DiagnosticOutcome outcome,
    string evidence, string nextStep, Uri supportUri)
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public DiagnosticOutcome Outcome { get; } = outcome;
    public string Evidence { get; } = evidence;
    public string NextStep { get; } = nextStep;
    public Uri SupportUri { get; } = supportUri;
    public string OutcomeLabel => Outcome switch
    {
        DiagnosticOutcome.Passed => "PASS",
        DiagnosticOutcome.Attention => "ATTENTION",
        DiagnosticOutcome.Unknown => "UNKNOWN",
        _ => "GUIDED CHECK",
    };
}

// Counts and a deployment-mode flag only: no device IDs, names, addresses or secrets.
public sealed record ConnectionDiagnosticSnapshot(DateTimeOffset CapturedAt,
    int? AppleUsbNodes, int? UsbNodesWithProblems, int? UsbNodesWithUnknownStatus,
    bool? AppleDevicesInstalled, string? AppleDevicesVersion, int? LocalEndpointCount,
    int? TrustedCompanions, int? ConnectedCompanions, IReadOnlyList<string> UnavailableProbes)
{
    public bool? HasPackageIdentity { get; init; }
}

public sealed record ConnectionDiagnosticReport(ConnectionDiagnosticSnapshot Snapshot,
    IReadOnlyList<DiagnosticFinding> Findings)
{
    public string ToShareableText() => string.Join(Environment.NewLine,
        new[] { "Phone Link — connection diagnostics", Snapshot.CapturedAt.ToString("O", CultureInfo.InvariantCulture),
            "Read-only snapshot. No phone data, device identifiers, or network addresses included." }
        .Concat(Findings.Select(finding =>
            $"[{finding.OutcomeLabel}] {finding.Title}\n{finding.Evidence}\nNext: {finding.NextStep}")));
}

public static class ConnectionDiagnosticEvaluator
{
    private static readonly Uri ConnectionGuide = new("https://support.apple.com/108643");
    private static readonly Uri TrustGuide = new("https://support.apple.com/109054");
    private static readonly Uri RecoveryGuide = new("https://support.apple.com/118106");

    public static ConnectionDiagnosticReport Evaluate(ConnectionDiagnosticSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        List<DiagnosticFinding> findings = [];
        findings.Add(new("app-identity", "Windows app identity", DiagnosticOutcome.Guidance,
            snapshot.HasPackageIdentity switch
            {
                true => "This process has Windows package identity.",
                false => "This process is running without Windows package identity (standalone executable).",
                _ => "Package identity was not captured; deployment mode is unknown.",
            },
            "Identity alone does not explain Bluetooth AccessDenied. Compare the same source in a current signed package without disabling security. Packaged and standalone protected data locations differ; this scan does not install a package, trust a certificate, or migrate pairing data.",
            new Uri("https://learn.microsoft.com/en-us/windows/win32/api/appmodel/nf-appmodel-getcurrentpackagefullname")));
        findings.Add(new("usb", "Apple USB presence",
            snapshot.AppleUsbNodes is null ? DiagnosticOutcome.Unknown :
                snapshot.AppleUsbNodes > 0 ? DiagnosticOutcome.Passed : DiagnosticOutcome.Attention,
            snapshot.AppleUsbNodes is { } nodes
                ? $"Windows enumerated {nodes} present Apple-vendor USB device node(s). These are not necessarily iPhones or separate physical devices."
                : "Windows USB enumeration did not complete. This is not evidence that the phone is disconnected.",
            "For a cable connection, unlock the iPhone and connect it directly to the PC using a data-capable cable. Charging alone does not establish data access.", ConnectionGuide));
        findings.Add(new("driver", "Windows device/driver status",
            snapshot.AppleUsbNodes is null or 0 ? DiagnosticOutcome.Unknown :
                snapshot.UsbNodesWithProblems > 0 ? DiagnosticOutcome.Attention :
                snapshot.UsbNodesWithProblems is null || snapshot.UsbNodesWithUnknownStatus is null or > 0 ? DiagnosticOutcome.Unknown : DiagnosticOutcome.Passed,
            snapshot.AppleUsbNodes is null or 0 ? "No present Apple USB nodes are available for a driver-status check." :
                $"{snapshot.UsbNodesWithProblems?.ToString(CultureInfo.InvariantCulture) ?? "Unknown"} node(s) report a Windows problem; {snapshot.UsbNodesWithUnknownStatus?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} have unavailable status. A clean status does not prove iOS trust or hardware health.",
            "If Windows reports a problem, inspect the device in Device Manager and use official Windows/Apple software updates. This scan does not remove or replace drivers.", ConnectionGuide));
        findings.Add(new("apple-devices", "Apple Devices for Windows",
            snapshot.AppleDevicesInstalled switch { true => DiagnosticOutcome.Passed, false => DiagnosticOutcome.Attention, _ => DiagnosticOutcome.Unknown },
            snapshot.AppleDevicesInstalled switch
            {
                true => "Apple Devices is registered for the current Windows user.",
                false => "The current-user package inventory did not contain Apple Devices. This does not check legacy iTunes installations.",
                _ => "The current-user app inventory could not be read; installation status is unknown.",
            },
            "Use Apple's Apple Devices app for supported USB backups, updates, and restoration. Its USB Trust relationship is separate from Phone Link companion pairing.", TrustGuide));
        findings.Add(new("network", "Companion local-network path",
            snapshot.LocalEndpointCount is null ? DiagnosticOutcome.Unknown :
                snapshot.LocalEndpointCount > 0 ? DiagnosticOutcome.Passed : DiagnosticOutcome.Attention,
            snapshot.LocalEndpointCount is { } endpoints ? $"{endpoints} eligible local pairing address(es) found. This does not prove firewall access or that the iPhone shares this network."
                : "Local network-interface information is unavailable.",
            "Keep the companion and PC on a trusted local network. Do not disable your firewall; check the app's allowed access instead.", ConnectionGuide));
        findings.Add(new("companion", "Phone Link companion session",
            snapshot.ConnectedCompanions > 0 ? DiagnosticOutcome.Passed :
                snapshot.TrustedCompanions is null ? DiagnosticOutcome.Unknown : DiagnosticOutcome.Attention,
            snapshot.TrustedCompanions is { } peers && snapshot.ConnectedCompanions is { } connected
                ? $"{peers} trusted companion(s); {connected} currently authenticated session(s). USB detection alone cannot establish this encrypted app connection."
                : "Companion trust/session information is unavailable.",
            "Install this project's iPhone companion, open Pair with Windows, and scan a fresh code inside the companion. Keep it in the foreground to connect.", TrustGuide));
        findings.Add(new("recovery", "Update, recovery, and hardware diagnosis", DiagnosticOutcome.Guidance,
            "This connection scan cannot diagnose a boot loop, certify battery health, or establish a hardware fault.",
            "Back up first when possible. Use the symptom-specific Apple recovery or diagnostics workflow. Restore erases data; no restore, reset, or firmware operation is performed by this scan.", RecoveryGuide));
        return new(snapshot, findings);
    }
}
