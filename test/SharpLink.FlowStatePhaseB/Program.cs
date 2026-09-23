using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpLink.FlowStatePhaseB;

internal static class Program
{
    internal static async Task Main(string[] args)
    {
        if (args.Contains("--self-test"))
        {
            await Checks.RunAsync();
            return;
        }
        var items = ReadInt(args, "--items", 10000);
        var repetitions = ReadInt(args, "--repetitions", 3);
        var output = Read(args, "--output") ?? "phase-b.json";
        var diagnose = args.Contains("--diagnose");
        var rows = new List<Result>();
        if (!args.Contains("--grants-only"))
            DirectionalProbe.Run(items, repetitions, diagnose, rows);
        if (!args.Contains("--b0-only"))
            await GrantProbe.RunAsync(items, repetitions, rows);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(rows, EvidenceJson.Default.ListResult));
        Console.WriteLine($"Wrote {rows.Count} rows to {output}");
    }

    private static string? Read(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int ReadInt(string[] args, string flag, int fallback)
    {
        var value = Read(args, flag);
        var parsed = value is null ? fallback : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parsed);
        return parsed;
    }
}

internal sealed record Result(string Family, string Variant, string Shape, int ActiveStreams,
    int Workers, int ItemBytes, int ItemsPerStream, int Repetition, double NsPerItem,
    double AllocatedBytesPerItem, double ConnectionGateEntriesPerItem, double OwnerHandoffsPerItem,
    double QueueOperationsPerItem, double ExplicitSubmissionRmwPerItem, double? RuntimeAtomicRmwPerItem,
    double GateWaitNsPerItem, double GateHoldNsPerItem, bool Instrumented, long Checksum);

[JsonSerializable(typeof(List<Result>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class EvidenceJson : JsonSerializerContext;
