using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using PhoneLink.Application.Features.BinaryInspection;

var lookup = args.Length == 3 && args[1] == "--file-offset";
var virtualLookup = (args.Length == 3 || (args.Length == 5 && args[3] == "--slice")) && args[1] == "--virtual-address";
var runtimeLookup = (args.Length == 3 || (args.Length == 5 && args[3] is "--symbol" or "--address")) && args[1] == "--runtime";
var snapshotAddressLookup = runtimeLookup && args.Length == 5 && args[3] == "--address";
if (args.Length != 1 && !lookup && !virtualLookup && !runtimeLookup)
{
    Console.Error.WriteLine("Usage: PhoneLink.OffsetDumper <local-Mach-O-file> [--file-offset <0xHEX> | --virtual-address <0xHEX> [--slice <zero-based-index>] | --runtime <reviewed-companion-JSON> [--symbol <exact-name> | --address <0xHEX>]]. No device memory is read and no binary is executed.");
    return 2;
}
ulong offset = 0;
if ((lookup || virtualLookup || snapshotAddressLookup) &&
    !MachOFileOffsetLookup.TryParseHex(args[snapshotAddressLookup ? 4 : 2], out offset))
{
    Console.Error.WriteLine("Supply a hexadecimal value with a 0x prefix, for example 0x204.");
    return 2;
}
var sliceIndex = 0;
if (virtualLookup && args.Length == 5 &&
    (!int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out sliceIndex) || sliceIndex < 0))
{
    Console.Error.WriteLine("Supply a zero-based slice index.");
    return 2;
}
using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
Console.CancelKeyPress += cancel;
try
{
    await using var stream = new FileStream(args[0], FileMode.Open, FileAccess.Read, FileShare.Read,
        64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    var report = await MachOInspector.InspectAsync(stream, cancellation.Token);
    object output = report;
    if (lookup)
    {
        output = new { report.Sha256, report.ByteLength, report.RuntimeAddressesAvailable, Lookup = MachOFileOffsetLookup.Lookup(report, offset) };
    }
    else if (virtualLookup)
    {
        if (report.Images.Count != 1 && args.Length != 5 || sliceIndex >= report.Images.Count)
        {
            Console.Error.WriteLine("Choose a valid --slice index. It is required for a universal image with multiple slices.");
            return 2;
        }
        output = new { report.Sha256, report.ByteLength, report.RuntimeAddressesAvailable,
            Lookup = MachOVirtualAddressLookup.Lookup(report, sliceIndex, offset) };
    }
    else if (runtimeLookup)
    {
        await using var snapshot = new FileStream(args[2], FileMode.Open, FileAccess.Read, FileShare.Read,
            8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (snapshot.Length > CompanionRuntimeMapper.MaximumSnapshotBytes)
            throw new InvalidDataException("Runtime snapshot exceeds 8 KiB.");
        var bytes = new byte[checked((int)snapshot.Length)];
        await snapshot.ReadExactlyAsync(bytes, cancellation.Token);
        output = snapshotAddressLookup
            ? new { report.Sha256, report.ByteLength, report.RuntimeAddressesAvailable,
                Lookup = CompanionRuntimeMapper.LookupAddress(report, bytes, offset) }
            : new { StaticInspection = report, RuntimeTextMapping = CompanionRuntimeMapper.Map(report, bytes, args.Length == 5 ? args[4] : null) };
    }
    Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Inspection cancelled."); return 1; }
catch (Exception exception) when (exception is not OutOfMemoryException)
{
    Console.Error.WriteLine($"Inspection failed ({exception.GetType().Name}): {exception.Message}");
    return 1;
}
finally { Console.CancelKeyPress -= cancel; }
