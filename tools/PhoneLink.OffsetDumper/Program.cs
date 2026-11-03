using System.Text.Json;
using PhoneLink.Application.Features.BinaryInspection;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: PhoneLink.OffsetDumper <local-Mach-O-file>. Static inspection only; no device memory is read and no binary is executed.");
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
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Static inspection cancelled."); return 1; }
catch (Exception exception) when (exception is not OutOfMemoryException)
{
    Console.Error.WriteLine($"Static inspection failed ({exception.GetType().Name}): {exception.Message}");
    return 1;
}
finally { Console.CancelKeyPress -= cancel; }
