using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.Sdk;

namespace SharpLink.Benchmarks;

/// <summary>
/// Prints exact, non-timed cold-writer growth evidence for a generated DTO that mixes
/// direct strings, fixed scalars, and a nullable fixed scalar. This is the cheap-sizing
/// matrix evidence: the fixed/nullable framing must participate in the pre-reserve hint.
/// </summary>
public static class GeneratedMixedDtoGrowthEvidenceRunner
{
    private const string NonAsciiSeed = "汉🙂";

    public static void Run()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build();
        var codec = context.Codecs.GetCodec<GeneratedMixedDirectDto>();

        foreach (var encodedBytes in GeneratedStringDtoCases.EncodedByteValues)
        {
            var payload = CreatePayload(codec, encodedBytes);
            using var writer = new PooledByteBufferWriter(GeneratedStringDtoCases.InitialCapacity);
            var tracking = new GrowthTrackingBufferWriter(writer);
            codec.Serialize(payload, tracking);

            if (writer.WrittenCount != encodedBytes)
            {
                throw new InvalidOperationException(
                    $"Generated mixed DTO wrote {writer.WrittenCount} bytes; expected {encodedBytes}.");
            }

            var transitions = string.Join(",", tracking.Transitions.Select(static transition =>
                $"{transition.PreviousCapacity}->{transition.NewCapacity}" +
                $"@{transition.WrittenBytes}+{transition.SizeHint}"));
            var copyRatio = ((double)tracking.CopiedBytes / writer.WrittenCount)
                .ToString("F4", CultureInfo.InvariantCulture);
            var capacityWasteRatio = ((double)(writer.Capacity - writer.WrittenCount) / writer.WrittenCount)
                .ToString("F4", CultureInfo.InvariantCulture);

            Console.WriteLine(
                $"[GeneratedMixedDtoGrowth] case={GeneratedStringDtoCases.Describe(encodedBytes)} " +
                $"encoded={encodedBytes} written={writer.WrittenCount} " +
                $"initialCapacity={GeneratedStringDtoCases.InitialCapacity} " +
                $"finalCapacity={writer.Capacity} growths={tracking.GrowthCount} " +
                $"copied={tracking.CopiedBytes} copyRatio={copyRatio} " +
                $"capacityWaste={writer.Capacity - writer.WrittenCount} " +
                $"capacityWasteRatio={capacityWasteRatio} transitions={transitions}");
        }
    }

    private static GeneratedMixedDirectDto CreatePayload(
        IRpcCodec<GeneratedMixedDirectDto> codec,
        int encodedBytes)
    {
        var shape = new GeneratedMixedDirectDto
        {
            Number = 42,
            Optional = 17,
            Flag = true,
            Ratio = 1.5,
            Id = Guid.Parse("5f66b6f6-1f7e-4f7d-9c58-000000000000")
        };

        using var framingWriter = new PooledByteBufferWriter(GeneratedStringDtoCases.InitialCapacity);
        codec.Serialize(shape, framingWriter);
        var framingBytes = framingWriter.WrittenCount;
        if (encodedBytes < framingBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(encodedBytes),
                $"Encoded target {encodedBytes} is smaller than mixed-DTO framing {framingBytes}.");
        }

        var contentBytes = encodedBytes - framingBytes;
        var values = CreateUtf8Values(contentBytes, 3);
        return new GeneratedMixedDirectDto
        {
            Text1 = values[0],
            Text2 = values[1],
            Text3 = values[2],
            Number = shape.Number,
            Optional = shape.Optional,
            Flag = shape.Flag,
            Ratio = shape.Ratio,
            Id = shape.Id
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
public sealed class GeneratedMixedDirectDto
{
    [RpcMember(1)] public string Text1 { get; set; } = string.Empty;
    [RpcMember(2)] public string Text2 { get; set; } = string.Empty;
    [RpcMember(3)] public string Text3 { get; set; } = string.Empty;
    [RpcMember(4)] public int Number { get; set; }
    [RpcMember(5)] public int? Optional { get; set; }
    [RpcMember(6)] public bool Flag { get; set; }
    [RpcMember(7)] public double Ratio { get; set; }
    [RpcMember(8)] public Guid Id { get; set; }
}
