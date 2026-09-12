using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.Sdk;

namespace SharpLink.Benchmarks;

/// <summary>
/// Prints exact, non-timed cold-writer growth evidence for a generated string collection.
/// </summary>
public static class GeneratedStringCollectionGrowthEvidenceRunner
{
    public static void Run()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build();
        var codec = context.Codecs.GetCodec<List<string>>();

        foreach (var count in new[] { 1, 4, 16, 64, 256, 1024, 4096, 16384 })
        {
            var value = Enumerable.Range(0, count)
                .Select(i => new string('x', 64) + i)
                .ToList();
            using var writer = new PooledByteBufferWriter(GeneratedStringDtoCases.InitialCapacity);
            var tracking = new GrowthTrackingBufferWriter(writer);
            codec.Serialize(value, tracking);

            var copyRatio = ((double)tracking.CopiedBytes / writer.WrittenCount)
                .ToString("F4", CultureInfo.InvariantCulture);
            var capacityWasteRatio = ((double)(writer.Capacity - writer.WrittenCount) / writer.WrittenCount)
                .ToString("F4", CultureInfo.InvariantCulture);
            Console.WriteLine(
                $"[GeneratedStringCollectionGrowth] count={count} written={writer.WrittenCount} " +
                $"initialCapacity={GeneratedStringDtoCases.InitialCapacity} " +
                $"finalCapacity={writer.Capacity} growths={tracking.GrowthCount} " +
                $"copied={tracking.CopiedBytes} copyRatio={copyRatio} " +
                $"capacityWaste={writer.Capacity - writer.WrittenCount} " +
                $"capacityWasteRatio={capacityWasteRatio}");
        }
    }
}

[RpcSerializable]
public sealed class GeneratedStringListDto
{
    [RpcMember(1)] public List<string> Values { get; set; } = new();
}
