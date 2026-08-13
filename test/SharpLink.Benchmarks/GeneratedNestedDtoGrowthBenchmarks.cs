using System;
using System.Buffers;
using System.Globalization;
using System.Linq;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.Sdk;

namespace SharpLink.Benchmarks;

/// <summary>
/// Prints exact, non-timed cold-writer growth evidence for a generated DTO that has direct
/// strings plus one nested DTO. The root codec can only reserve the direct-member lower bound;
/// the nested member still uses its own length backfill and may grow independently.
/// </summary>
public static class GeneratedNestedDtoGrowthEvidenceRunner
{
    private const string NonAsciiSeed = "汉🙂";

    public static void Run()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build();
        var codec = context.Codecs.GetCodec<GeneratedNestedDto>();

        foreach (var encodedBytes in GeneratedStringDtoCases.EncodedByteValues)
        {
            var payload = CreatePayload(codec, encodedBytes);
            using var writer = new PooledByteBufferWriter(GeneratedStringDtoCases.InitialCapacity);
            var tracking = new GrowthTrackingBufferWriter(writer);
            codec.Serialize(payload, tracking);

            if (writer.WrittenCount != encodedBytes)
            {
                throw new InvalidOperationException(
                    $"Generated nested DTO wrote {writer.WrittenCount} bytes; expected {encodedBytes}.");
            }

            var transitions = string.Join(",", tracking.Transitions.Select(static transition =>
                $"{transition.PreviousCapacity}->{transition.NewCapacity}" +
                $"@{transition.WrittenBytes}+{transition.SizeHint}"));
            var copyRatio = ((double)tracking.CopiedBytes / writer.WrittenCount)
                .ToString("F4", CultureInfo.InvariantCulture);
            var capacityWasteRatio = ((double)(writer.Capacity - writer.WrittenCount) / writer.WrittenCount)
                .ToString("F4", CultureInfo.InvariantCulture);

            Console.WriteLine(
                $"[GeneratedNestedDtoGrowth] case={GeneratedStringDtoCases.Describe(encodedBytes)} " +
                $"encoded={encodedBytes} written={writer.WrittenCount} " +
                $"initialCapacity={GeneratedStringDtoCases.InitialCapacity} " +
                $"finalCapacity={writer.Capacity} growths={tracking.GrowthCount} " +
                $"copied={tracking.CopiedBytes} copyRatio={copyRatio} " +
                $"capacityWaste={writer.Capacity - writer.WrittenCount} " +
                $"capacityWasteRatio={capacityWasteRatio} transitions={transitions}");
        }
    }

    private static GeneratedNestedDto CreatePayload(
        IRpcCodec<GeneratedNestedDto> codec,
        int encodedBytes)
    {
        var shape = new GeneratedNestedDto
        {
            Number = 42,
            Nested = new GeneratedNestedFixedDto { Value = 7 }
        };

        using var framingWriter = new PooledByteBufferWriter(GeneratedStringDtoCases.InitialCapacity);
        codec.Serialize(shape, framingWriter);
        var framingBytes = framingWriter.WrittenCount;
        if (encodedBytes < framingBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(encodedBytes),
                $"Encoded target {encodedBytes} is smaller than nested-DTO framing {framingBytes}.");
        }

        var contentBytes = encodedBytes - framingBytes;
        var values = CreateUtf8Values(contentBytes, 2);
        return new GeneratedNestedDto
        {
            Text1 = values[0],
            Text2 = values[1],
            Number = shape.Number,
            Nested = shape.Nested
        };
    }

    private static string[] CreateUtf8Values(int contentBytes, int fieldCount)
    {
        var seedBytes = System.Text.Encoding.UTF8.GetByteCount(NonAsciiSeed);
        var values = new string[fieldCount];
        var baseBytes = contentBytes / fieldCount;
        var remainder = contentBytes % fieldCount;
        for (var index = 0; index < values.Length; index++)
        {
            var fieldBytes = baseBytes + (index < remainder ? 1 : 0);
            values[index] = NonAsciiSeed + new string('x', Math.Max(0, fieldBytes - seedBytes));
        }
        return values;
    }
}

[RpcSerializable]
public sealed class GeneratedNestedDto
{
    [RpcMember(1)] public string Text1 { get; set; } = string.Empty;
    [RpcMember(2)] public string Text2 { get; set; } = string.Empty;
    [RpcMember(3)] public int Number { get; set; }
    [RpcMember(4)] public GeneratedNestedFixedDto Nested { get; set; } = new();
}

[RpcSerializable]
public sealed class GeneratedNestedFixedDto
{
    [RpcMember(1)] public int Value { get; set; }
}
