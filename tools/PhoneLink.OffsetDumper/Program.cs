using System.Text.Json;
using PhoneLink.Application.Features.BinaryInspection;

if (args.Length != 1 && !(args.Length == 3 && args[1] == "--runtime"))
{
    Console.Error.WriteLine("Usage: PhoneLink.OffsetDumper <local-Mach-O-file> [--runtime <reviewed-companion-JSON>]. Optional text mapping is snapshot-derived, not verified live. No device memory is read and no binary is executed.");
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
    if (args.Length == 3)
    {
        await using var snapshot = new FileStream(args[2], FileMode.Open, FileAccess.Read, FileShare.Read,
            8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (snapshot.Length > CompanionRuntimeMapper.MaximumSnapshotBytes)
            throw new InvalidDataException("Runtime snapshot exceeds 8 KiB.");
        var bytes = new byte[checked((int)snapshot.Length)];
        await snapshot.ReadExactlyAsync(bytes, cancellation.Token);
        var mapping = CompanionRuntimeMapper.Map(report, bytes);
        output = new { StaticInspection = report, RuntimeTextMapping = mapping };
    }
    Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Inspection cancelled."); return 1; }
catch (Exception exception) when (exception is not OutOfMemoryException)
{
    Console.Error.WriteLine($"Inspection failed ({exception.GetType().Name}): {exception.Message}");
    return 1;
}
finally { Console.CancelKeyPress -= cancel; }
