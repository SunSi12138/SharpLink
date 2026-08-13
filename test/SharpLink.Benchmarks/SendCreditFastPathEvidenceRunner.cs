using System;
using System.Buffers;
using System.Diagnostics;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

public static class SendCreditFastPathEvidenceRunner
{
    public static void Run()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build();
        var codec = context.Codecs.GetCodec<GeneratedNestedDto>();
        var payload = new GeneratedNestedDto
        {
            Text1 = "汉🙂",
            Text2 = new string('x', 512),
            Number = 42,
            Nested = new GeneratedNestedFixedDto { Value = 7 }
        };

        const int iterations = 200_000;
        using var writer = new PooledByteBufferWriter(4 * 1024 * 1024);

        codec.Serialize(payload, writer);
        writer.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var oldAllocated = GC.GetAllocatedBytesForCurrentThread();
        var oldStart = Stopwatch.GetTimestamp();
        for (var index = 0; index < iterations; index++)
        {
            codec.Serialize(payload, writer);
            writer.Clear();
        }
        var oldElapsed = Stopwatch.GetElapsedTime(oldStart);
        var oldAllocatedDelta = GC.GetAllocatedBytesForCurrentThread() - oldAllocated;

        var sized = (IRpcSizedCodec<GeneratedNestedDto>)codec;
        sized.TryGetEncodedSize(payload, out _, out var snapshot);
        sized.SerializeSized(payload, writer, 0, snapshot);
        sized.ReleaseSnapshot(snapshot);
        writer.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var newAllocated = GC.GetAllocatedBytesForCurrentThread();
        var newStart = Stopwatch.GetTimestamp();
        for (var index = 0; index < iterations; index++)
        {
            sized.TryGetEncodedSize(payload, out var encodedSize, out var sizedSnapshot);
            sized.SerializeSized(payload, writer, encodedSize, sizedSnapshot);
            sized.ReleaseSnapshot(sizedSnapshot);
            writer.Clear();
        }
        var newElapsed = Stopwatch.GetElapsedTime(newStart);
        var newAllocatedDelta = GC.GetAllocatedBytesForCurrentThread() - newAllocated;

        Console.WriteLine(
            $"[SendCreditFastPath] iterations={iterations} " +
            $"serializeFirstNsPerOp={(oldElapsed.TotalNanoseconds / iterations):F2} " +
            $"sizedPathNsPerOp={(newElapsed.TotalNanoseconds / iterations):F2} " +
            $"serializeFirstAllocatedPerOp={(oldAllocatedDelta / (double)iterations):F3} " +
            $"sizedPathAllocatedPerOp={(newAllocatedDelta / (double)iterations):F3}");
    }
}
