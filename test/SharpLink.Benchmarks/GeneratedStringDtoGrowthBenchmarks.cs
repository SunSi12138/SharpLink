using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.Sdk;

namespace SharpLink.Benchmarks;

/// <summary>
/// Measures the production source-generated DTO codec across direct-string field counts and exact
/// encoded-size boundaries. This class intentionally defines no source job so <c>--job Dry</c>
/// remains validation-only and final runs can select their job explicitly.
/// </summary>
[BenchmarkCategory("Issue92", "GeneratedStrings")]
[MemoryDiagnoser(displayGenColumns: false)]
public class GeneratedStringDtoGrowthBenchmarks
{
    private SharpLinkRuntimeContext _context = null!;
    private SharpLinkBufferWriterPool _pool = null!;
    private GeneratedStringDtoScenario _scenario = null!;

    [Params(1, 4, 16, 64)]
    public int FieldCount { get; set; }

    [ParamsSource(nameof(EncodedByteCases))]
    public int EncodedBytes { get; set; }

    public static IEnumerable<int> EncodedByteCases => GeneratedStringDtoCases.EncodedByteValues;

    [GlobalSetup]
    public void Setup()
    {
        _context = new SharpLinkRuntimeContextBuilder().Build();
        _pool = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions
        {
            InitialCapacity = GeneratedStringDtoCases.InitialCapacity,
            MaxPooledWriters = 1,
            MaxRetainedCapacityBytes = BufferWriterPoolOptions.MaximumRetainedCapacityBytes
        });
        _scenario = GeneratedStringDtoScenario.Create(_context, FieldCount, EncodedBytes);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _pool.Dispose();
        _context.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int SerializeGeneratedDtoBaseline()
    {
        var writer = _pool.Rent();
        try
        {
            _scenario.Serialize(writer);
            return writer.WrittenCount;
        }
        finally
        {
            _pool.Return(writer);
        }
    }
}

/// <summary>Prints exact, non-timed cold-writer growth evidence for every generated DTO case.</summary>
public static class GeneratedStringDtoGrowthEvidenceRunner
{
    public static void Run()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build();
        foreach (var fieldCount in GeneratedStringDtoCases.FieldCounts)
        {
            foreach (var encodedBytes in GeneratedStringDtoCases.EncodedByteValues)
            {
                var scenario = GeneratedStringDtoScenario.Create(context, fieldCount, encodedBytes);
                using var writer = new PooledByteBufferWriter(GeneratedStringDtoCases.InitialCapacity);
                var trackingWriter = new GrowthTrackingBufferWriter(writer);
                scenario.Serialize(trackingWriter);
                if (writer.WrittenCount != encodedBytes)
                {
                    throw new InvalidOperationException(
                        $"Generated DTO wrote {writer.WrittenCount} bytes; expected {encodedBytes}.");
                }

                var transitions = string.Join(",", trackingWriter.Transitions.Select(static transition =>
                    $"{transition.PreviousCapacity}->{transition.NewCapacity}" +
                    $"@{transition.WrittenBytes}+{transition.SizeHint}"));
                var copyRatio = ((double)trackingWriter.CopiedBytes / writer.WrittenCount)
                    .ToString("F4", CultureInfo.InvariantCulture);
                var capacityWasteRatio = ((double)(writer.Capacity - writer.WrittenCount) / writer.WrittenCount)
                    .ToString("F4", CultureInfo.InvariantCulture);
                Console.WriteLine(
                    $"[GeneratedStringDtoGrowth] case={GeneratedStringDtoCases.Describe(encodedBytes)} " +
                    $"encoded={encodedBytes} fields={fieldCount} written={writer.WrittenCount} " +
                    $"initialCapacity={GeneratedStringDtoCases.InitialCapacity} " +
                    $"finalCapacity={writer.Capacity} growths={trackingWriter.GrowthCount} " +
                    $"copied={trackingWriter.CopiedBytes} " +
                    $"copyRatio={copyRatio} " +
                    $"capacityWaste={writer.Capacity - writer.WrittenCount} " +
                    $"capacityWasteRatio={capacityWasteRatio} " +
                    $"transitions={transitions}");
            }
        }
    }
}

internal static class GeneratedStringDtoCases
{
    internal const int InitialCapacity = 1024;

    internal static IReadOnlyList<int> FieldCounts { get; } = Array.AsReadOnly([1, 4, 16, 64]);

    // These are exact final wire sizes, including generated object and field framing, so every
    // field-count shape crosses the same writer boundary. The required scale remains
    // 1/4/16/64/256 KiB and 1 MiB. Targeted +/-1 cases cover the 1-KiB initial capacity, the
    // 64-KiB maximum retained bucket, and the first 128-KiB ArrayPool bucket above retention;
    // later candidates can add a boundary only where their observed transition needs one.
    internal static IReadOnlyList<int> EncodedByteValues { get; } = Array.AsReadOnly(
    [
        1023,
        1024,
        1025,
        4 * 1024,
        16 * 1024,
        64 * 1024 - 1,
        64 * 1024,
        64 * 1024 + 1,
        128 * 1024 - 1,
        128 * 1024,
        128 * 1024 + 1,
        256 * 1024,
        1024 * 1024
    ]);

    internal static string Describe(int encodedBytes)
        => encodedBytes switch
        {
            InitialCapacity - 1 => "initial-minus-one",
            InitialCapacity => "initial",
            InitialCapacity + 1 => "initial-plus-one",
            64 * 1024 - 1 => "retained-bucket-minus-one",
            64 * 1024 => "retained-bucket",
            64 * 1024 + 1 => "retained-bucket-plus-one",
            128 * 1024 - 1 => "growth-bucket-minus-one",
            128 * 1024 => "growth-bucket",
            128 * 1024 + 1 => "growth-bucket-plus-one",
            _ => "scale"
        };
}

internal sealed class GeneratedStringDtoScenario
{
    private readonly Action<IBufferWriter<byte>> _serialize;

    private GeneratedStringDtoScenario(
        int fieldCount,
        int encodedBytes,
        Action<IBufferWriter<byte>> serialize)
    {
        FieldCount = fieldCount;
        EncodedBytes = encodedBytes;
        _serialize = serialize;
    }

    internal int FieldCount { get; }

    internal int EncodedBytes { get; }

    internal void Serialize(IBufferWriter<byte> writer) => _serialize(writer);

    internal static GeneratedStringDtoScenario Create(
        SharpLinkRuntimeContext context,
        int fieldCount,
        int encodedBytes)
        => fieldCount switch
        {
            1 => Create<GeneratedStringPayload1>(context, fieldCount, encodedBytes),
            4 => Create<GeneratedStringPayload4>(context, fieldCount, encodedBytes),
            16 => Create<GeneratedStringPayload16>(context, fieldCount, encodedBytes),
            64 => Create<GeneratedStringPayload64>(context, fieldCount, encodedBytes),
            _ => throw new ArgumentOutOfRangeException(nameof(fieldCount))
        };

    private static GeneratedStringDtoScenario Create<T>(
        SharpLinkRuntimeContext context,
        int fieldCount,
        int encodedBytes)
        where T : class, new()
    {
        var codec = context.Codecs.GetCodec<T>();
        var emptyPayload = new T();
        var framingBytes = MeasureEncodedBytes(codec, emptyPayload);
        if (encodedBytes < framingBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(encodedBytes),
                $"Encoded target {encodedBytes} is smaller than {fieldCount}-field framing {framingBytes}.");
        }

        var values = CreateFieldValues(encodedBytes - framingBytes, fieldCount);
        var payload = CreatePayload<T>(values);
        var scenario = new GeneratedStringDtoScenario(
            fieldCount,
            encodedBytes,
            writer => codec.Serialize(payload, writer));

        using var verificationWriter = new PooledByteBufferWriter(
            Math.Max(GeneratedStringDtoCases.InitialCapacity, encodedBytes));
        scenario.Serialize(verificationWriter);
        if (verificationWriter.WrittenCount != encodedBytes)
        {
            throw new InvalidOperationException(
                $"Generated {typeof(T).Name} encoded {verificationWriter.WrittenCount} bytes; " +
                $"expected {encodedBytes}.");
        }

        return scenario;
    }

    private static int MeasureEncodedBytes<T>(IRpcCodec<T> codec, T payload)
    {
        using var writer = new PooledByteBufferWriter(GeneratedStringDtoCases.InitialCapacity);
        codec.Serialize(payload, writer);
        return writer.WrittenCount;
    }

    private static string[] CreateFieldValues(int contentBytes, int fieldCount)
    {
        var values = new string[fieldCount];
        var baseLength = contentBytes / fieldCount;
        var remainder = contentBytes % fieldCount;
        for (var index = 0; index < values.Length; index++)
            values[index] = new string('x', baseLength + (index < remainder ? 1 : 0));
        return values;
    }

    private static T CreatePayload<T>(IReadOnlyList<string> values)
        where T : class, new()
    {
        var properties = typeof(T)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(static property => new
            {
                Property = property,
                Member = property.GetCustomAttribute<RpcMemberAttribute>()
            })
            .Where(static value => value.Member is not null)
            .OrderBy(static value => value.Member!.Id)
            .ToArray();
        if (properties.Length != values.Count)
        {
            throw new InvalidOperationException(
                $"Generated DTO {typeof(T).Name} has {properties.Length} string fields; " +
                $"expected {values.Count}.");
        }

        var payload = new T();
        for (var index = 0; index < properties.Length; index++)
            properties[index].Property.SetValue(payload, values[index]);
        return payload;
    }
}

[RpcSerializable]
public sealed class GeneratedStringPayload1
{
    [RpcMember(1)] public string Field01 { get; set; } = string.Empty;
}

[RpcSerializable]
public sealed class GeneratedStringPayload4
{
    [RpcMember(1)] public string Field01 { get; set; } = string.Empty;
    [RpcMember(2)] public string Field02 { get; set; } = string.Empty;
    [RpcMember(3)] public string Field03 { get; set; } = string.Empty;
    [RpcMember(4)] public string Field04 { get; set; } = string.Empty;
}

[RpcSerializable]
public sealed class GeneratedStringPayload16
{
    [RpcMember(1)] public string Field01 { get; set; } = string.Empty;
    [RpcMember(2)] public string Field02 { get; set; } = string.Empty;
    [RpcMember(3)] public string Field03 { get; set; } = string.Empty;
    [RpcMember(4)] public string Field04 { get; set; } = string.Empty;
    [RpcMember(5)] public string Field05 { get; set; } = string.Empty;
    [RpcMember(6)] public string Field06 { get; set; } = string.Empty;
    [RpcMember(7)] public string Field07 { get; set; } = string.Empty;
    [RpcMember(8)] public string Field08 { get; set; } = string.Empty;
    [RpcMember(9)] public string Field09 { get; set; } = string.Empty;
    [RpcMember(10)] public string Field10 { get; set; } = string.Empty;
    [RpcMember(11)] public string Field11 { get; set; } = string.Empty;
    [RpcMember(12)] public string Field12 { get; set; } = string.Empty;
    [RpcMember(13)] public string Field13 { get; set; } = string.Empty;
    [RpcMember(14)] public string Field14 { get; set; } = string.Empty;
    [RpcMember(15)] public string Field15 { get; set; } = string.Empty;
    [RpcMember(16)] public string Field16 { get; set; } = string.Empty;
}

[RpcSerializable]
public sealed class GeneratedStringPayload64
{
    [RpcMember(1)] public string Field01 { get; set; } = string.Empty;
    [RpcMember(2)] public string Field02 { get; set; } = string.Empty;
    [RpcMember(3)] public string Field03 { get; set; } = string.Empty;
    [RpcMember(4)] public string Field04 { get; set; } = string.Empty;
    [RpcMember(5)] public string Field05 { get; set; } = string.Empty;
    [RpcMember(6)] public string Field06 { get; set; } = string.Empty;
    [RpcMember(7)] public string Field07 { get; set; } = string.Empty;
    [RpcMember(8)] public string Field08 { get; set; } = string.Empty;
    [RpcMember(9)] public string Field09 { get; set; } = string.Empty;
    [RpcMember(10)] public string Field10 { get; set; } = string.Empty;
    [RpcMember(11)] public string Field11 { get; set; } = string.Empty;
    [RpcMember(12)] public string Field12 { get; set; } = string.Empty;
    [RpcMember(13)] public string Field13 { get; set; } = string.Empty;
    [RpcMember(14)] public string Field14 { get; set; } = string.Empty;
    [RpcMember(15)] public string Field15 { get; set; } = string.Empty;
    [RpcMember(16)] public string Field16 { get; set; } = string.Empty;
    [RpcMember(17)] public string Field17 { get; set; } = string.Empty;
    [RpcMember(18)] public string Field18 { get; set; } = string.Empty;
    [RpcMember(19)] public string Field19 { get; set; } = string.Empty;
    [RpcMember(20)] public string Field20 { get; set; } = string.Empty;
    [RpcMember(21)] public string Field21 { get; set; } = string.Empty;
    [RpcMember(22)] public string Field22 { get; set; } = string.Empty;
    [RpcMember(23)] public string Field23 { get; set; } = string.Empty;
    [RpcMember(24)] public string Field24 { get; set; } = string.Empty;
    [RpcMember(25)] public string Field25 { get; set; } = string.Empty;
    [RpcMember(26)] public string Field26 { get; set; } = string.Empty;
    [RpcMember(27)] public string Field27 { get; set; } = string.Empty;
    [RpcMember(28)] public string Field28 { get; set; } = string.Empty;
    [RpcMember(29)] public string Field29 { get; set; } = string.Empty;
    [RpcMember(30)] public string Field30 { get; set; } = string.Empty;
    [RpcMember(31)] public string Field31 { get; set; } = string.Empty;
    [RpcMember(32)] public string Field32 { get; set; } = string.Empty;
    [RpcMember(33)] public string Field33 { get; set; } = string.Empty;
    [RpcMember(34)] public string Field34 { get; set; } = string.Empty;
    [RpcMember(35)] public string Field35 { get; set; } = string.Empty;
    [RpcMember(36)] public string Field36 { get; set; } = string.Empty;
    [RpcMember(37)] public string Field37 { get; set; } = string.Empty;
    [RpcMember(38)] public string Field38 { get; set; } = string.Empty;
    [RpcMember(39)] public string Field39 { get; set; } = string.Empty;
    [RpcMember(40)] public string Field40 { get; set; } = string.Empty;
    [RpcMember(41)] public string Field41 { get; set; } = string.Empty;
    [RpcMember(42)] public string Field42 { get; set; } = string.Empty;
    [RpcMember(43)] public string Field43 { get; set; } = string.Empty;
    [RpcMember(44)] public string Field44 { get; set; } = string.Empty;
    [RpcMember(45)] public string Field45 { get; set; } = string.Empty;
    [RpcMember(46)] public string Field46 { get; set; } = string.Empty;
    [RpcMember(47)] public string Field47 { get; set; } = string.Empty;
    [RpcMember(48)] public string Field48 { get; set; } = string.Empty;
    [RpcMember(49)] public string Field49 { get; set; } = string.Empty;
    [RpcMember(50)] public string Field50 { get; set; } = string.Empty;
    [RpcMember(51)] public string Field51 { get; set; } = string.Empty;
    [RpcMember(52)] public string Field52 { get; set; } = string.Empty;
    [RpcMember(53)] public string Field53 { get; set; } = string.Empty;
    [RpcMember(54)] public string Field54 { get; set; } = string.Empty;
    [RpcMember(55)] public string Field55 { get; set; } = string.Empty;
    [RpcMember(56)] public string Field56 { get; set; } = string.Empty;
    [RpcMember(57)] public string Field57 { get; set; } = string.Empty;
    [RpcMember(58)] public string Field58 { get; set; } = string.Empty;
    [RpcMember(59)] public string Field59 { get; set; } = string.Empty;
    [RpcMember(60)] public string Field60 { get; set; } = string.Empty;
    [RpcMember(61)] public string Field61 { get; set; } = string.Empty;
    [RpcMember(62)] public string Field62 { get; set; } = string.Empty;
    [RpcMember(63)] public string Field63 { get; set; } = string.Empty;
    [RpcMember(64)] public string Field64 { get; set; } = string.Empty;
}
