using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;

if (args.Length != 3)
    throw new ArgumentException("usage: AllocationParser TRACE RESULT_JSON OUTPUT_JSON");

using var report = JsonDocument.Parse(File.ReadAllText(args[1]));
var root = report.RootElement;
var operations = root.GetProperty("operations").GetInt64();
var itemCount = root.GetProperty("itemCount").GetInt64();
var items = checked(operations * itemCount);
if (operations <= 0 || items <= 0 || root.GetProperty("validationFailures").GetInt32() != 0)
    throw new InvalidOperationException("The traced RPC result is incomplete or invalid.");

var ticks = new List<AllocationTick>();
var markers = new List<Marker>();
var markerErrors = new List<string>();
long lost;
using (var source = new EventPipeEventSource(args[0]))
{
    source.Dynamic.All += data =>
    {
        if (data.ProviderName != "SharpLink-735-AllocationWindow") return;
        if ((int)data.ID == 1)
            markers.Add(new Marker(data.TimeStampRelativeMSec, data.ProcessID, 1, 0, 0, 0));
        else if ((int)data.ID == 2)
            markers.Add(new Marker(data.TimeStampRelativeMSec, data.ProcessID, 2,
                Convert.ToInt64(data.PayloadValue(0), CultureInfo.InvariantCulture),
                Convert.ToInt64(data.PayloadValue(1), CultureInfo.InvariantCulture),
                Convert.ToInt64(data.PayloadValue(2), CultureInfo.InvariantCulture)));
        else if ((int)data.ID == 0)
            markerErrors.Add(data.FormattedMessage ?? data.ToString());
    };
    source.Clr.GCAllocationTick += data =>
    {
        ticks.Add(new AllocationTick(data.TimeStampRelativeMSec, data.ProcessID,
            data.TypeName ?? "<unnamed>", data.AllocationAmount64 > 0
                ? data.AllocationAmount64 : data.AllocationAmount,
            data.ObjectSize, data.AllocationKind.ToString()));
    };
    source.Process();
    var lostProperty = source.GetType().GetProperty("EventsLost")
        ?? throw new InvalidOperationException("The trace reader does not expose event loss.");
    lost = Convert.ToInt64(lostProperty.GetValue(source), CultureInfo.InvariantCulture);
}
if (lost != 0 || markerErrors.Count != 0)
    throw new InvalidOperationException($"Incomplete trace: lost={lost}; marker errors={string.Join("; ", markerErrors)}");
if (markers.Count != 2 || markers[0].Kind != 1 || markers[1].Kind != 2 ||
    markers[0].ProcessId != markers[1].ProcessId || markers[0].TimeMs >= markers[1].TimeMs ||
    markers[1].Operations != operations || markers[1].Items != items)
    throw new InvalidOperationException("The exact measurement-window markers do not match the RPC report.");
var begin = markers[0];
var end = markers[1];
var measured = ticks.Where(x => x.ProcessId == begin.ProcessId &&
    x.TimeMs >= begin.TimeMs && x.TimeMs <= end.TimeMs).ToArray();
if (measured.Length < 100 || measured.Any(x => x.IntervalBytes <= 0))
    throw new InvalidOperationException("Insufficient valid allocation samples in the measured window.");
var groups = measured.GroupBy(x => x.Type, StringComparer.Ordinal).Select(group => new
{
    type = group.Key,
    ticks = group.Count(),
    weightedBytes = group.Sum(x => x.IntervalBytes),
    estimatedBytesPerItem = group.Sum(x => x.IntervalBytes) / (double)items,
    representativeObjectSizes = group.Select(x => x.ObjectBytes).Distinct().Order().ToArray(),
    kinds = group.Select(x => x.Kind).Distinct().Order().ToArray()
}).OrderByDescending(x => x.weightedBytes).ToArray();
var result = new
{
    trace = Path.GetFileName(args[0]),
    commit = root.GetProperty("commit").GetString(),
    transport = root.GetProperty("transport").GetString(),
    scenario = root.GetProperty("scenario").GetString(),
    processId = begin.ProcessId,
    operations,
    items,
    windowBeginMs = begin.TimeMs,
    windowEndMs = end.TimeMs,
    eventsLost = lost,
    allocationTickSamples = measured.Length,
    measuredBytes = end.AllocatedBytes,
    measuredBytesPerItem = end.AllocatedBytes / (double)items,
    sampledIntervalBytes = measured.Sum(x => x.IntervalBytes),
    sampleCoverageRatio = measured.Sum(x => x.IntervalBytes) / (double)end.AllocatedBytes,
    caveat = "GCAllocationTick interval attribution is sampled, not exact per-type bytes. Profiling perturbs timing; this report is not a throughput acceptance result.",
    types = groups
};
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
File.WriteAllText(args[2], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"WINDOW pid={begin.ProcessId} items={items} ticks={measured.Length} loss={lost} bytes/item={result.measuredBytesPerItem:F3} coverage={result.sampleCoverageRatio:F3}");
foreach (var row in groups.Take(25))
    Console.WriteLine($"TYPE {row.estimatedBytesPerItem:F3} B/item ticks={row.ticks} sizes={string.Join(",", row.representativeObjectSizes)} {row.type}");

internal sealed record AllocationTick(double TimeMs, int ProcessId, string Type, long IntervalBytes, long ObjectBytes, string Kind);
internal sealed record Marker(double TimeMs, int ProcessId, int Kind, long Operations, long Items, long AllocatedBytes);
