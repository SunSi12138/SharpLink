using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;

if (args.Length != 1)
    return 2;

var rows = new Dictionary<string, (long Count, long Bytes)>();
using var source = new EventPipeEventSource(args[0]);
source.Clr.GCAllocationTick += data =>
{
    var frames = new List<string>();
    for (var frame = data.CallStack(); frame is not null && frames.Count < 20; frame = frame.Caller)
        frames.Add(frame.CodeAddress.FullMethodName);
    var key = $"{data.TypeName}\t{string.Join(" <- ", frames)}";
    rows.TryGetValue(key, out var current);
    rows[key] = (current.Count + 1, current.Bytes + (long)data.AllocationAmount64);
};
source.Process();

Console.WriteLine("samples\tsampled_bytes\ttype\tstack");
foreach (var row in rows.OrderByDescending(static x => x.Value.Bytes).Take(100))
    Console.WriteLine($"{row.Value.Count}\t{row.Value.Bytes}\t{row.Key}");
return 0;
