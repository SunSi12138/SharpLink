using System;
using System.Buffers;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

public static class GeneratedRecursiveAllocationEvidenceRunner
{
    public static void Run()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build();
        var nestedCodec = context.Codecs.GetCodec<GeneratedNestedDto>();
        var wrapperCodec = context.Codecs.GetCodec<GeneratedWrapperDto>();
        var directCodec = context.Codecs.GetCodec<GeneratedStringPayload1>();

        var nested = CreateNestedPayload();
        var wrapper = CreateWrapperPayload();
        var direct = new GeneratedStringPayload1 { Field01 = "汉🙂" };

        Measure("GeneratedNestedDto", nestedCodec, nested);
        Measure("GeneratedWrapperDto", wrapperCodec, wrapper);
        Measure("GeneratedStringPayload1", directCodec, direct);
    }

    private static void Measure<T>(string name, IRpcCodec<T> codec, T payload)
        where T : class
    {
        using var writer = new PooledByteBufferWriter(4 * 1024 * 1024);
        codec.Serialize(payload, writer);
        writer.Clear();

        const int iterations = 20_000;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++)
        {
            codec.Serialize(payload, writer);
            writer.Clear();
        }
        var after = GC.GetAllocatedBytesForCurrentThread();
        var allocatedPerOp = (double)(after - before) / iterations;
        Console.WriteLine(
            $"[GeneratedRecursiveAllocation] case={name} iterations={iterations} " +
            $"allocatedTotal={after - before} allocatedPerOp={allocatedPerOp:F2}");
    }

    private static GeneratedNestedDto CreateNestedPayload()
        => new()
        {
            Text1 = "汉🙂",
            Text2 = "value",
            Number = 42,
            Nested = new GeneratedNestedFixedDto { Value = 7 }
        };

    private static GeneratedWrapperDto CreateWrapperPayload()
        => new()
        {
            Content = new GeneratedWrapperContentDto
            {
                Text1 = "汉🙂",
                Text2 = "value",
                Number = 42
            }
        };
}
