using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal static class StreamCodecSpecializationEvidenceRunner
{
    private const int WarmupItems = 100_000;

    internal static async Task RunAsync(string[] args)
    {
        if (args.Length != 4)
        {
            throw new ArgumentException(
                "Usage: --stream-codec-specialization-evidence <interface|hoisted|specialized> <rounds> <items-per-round> <output-json>");
        }

        var mode = args[0] switch
        {
            "interface" => StreamCodecEvidenceMode.Interface,
            "hoisted" => StreamCodecEvidenceMode.Hoisted,
            "specialized" => StreamCodecEvidenceMode.Specialized,
            _ => throw new ArgumentOutOfRangeException(nameof(args), args[0], "Unknown stream Codec evidence mode.")
        };
        var rounds = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        var itemsPerRound = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rounds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(itemsPerRound);

        var outputPath = Path.GetFullPath(args[3]);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var measurements = new List<StreamCodecEvidenceMeasurement>();
        AddExactClassMeasurements(measurements, mode, rounds, itemsPerRound);
        AddUnsizedClassMeasurements(measurements, mode, rounds, itemsPerRound);
        AddExactStructMeasurements(measurements, mode, rounds, itemsPerRound);
        AddReceiveMeasurements(measurements, mode, rounds, itemsPerRound);

        var document = new StreamCodecEvidenceDocument
        {
            Configuration = Environment.GetEnvironmentVariable("SHARPLINK_CODEC_EVIDENCE_CONFIG") ?? "unspecified",
            Trial = int.TryParse(
                Environment.GetEnvironmentVariable("SHARPLINK_CODEC_EVIDENCE_TRIAL"),
                out var trial) ? trial : 0,
            Mode = mode.ToString().ToLowerInvariant(),
            Commit = Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA") ?? "unknown",
            Framework = RuntimeInformation.FrameworkDescription,
            Os = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            ProcessorCount = Environment.ProcessorCount,
            TieredCompilation = ReadEnvironment("DOTNET_TieredCompilation"),
            TieredPgo = ReadEnvironment("DOTNET_TieredPGO"),
            ReadyToRun = ReadEnvironment("DOTNET_ReadyToRun"),
            QuickJitForLoops = ReadEnvironment("DOTNET_TC_QuickJitForLoops"),
            OnStackReplacement = ReadEnvironment("DOTNET_TC_OnStackReplacement"),
            Rounds = rounds,
            ItemsPerRound = itemsPerRound,
            Measurements = measurements,
            Notes =
            [
                "interface reproduces the current per-item IRpcSizedCodec<T> type test and CanExactSize read before serialization.",
                "hoisted opens exact-sized capability once per synthetic stream and keeps TryGetEncodedSize per item.",
                "specialized keeps the concrete TCodec in the whole loop through constrained generic calls; sealed class codecs are the production-representative primary case and struct codecs are an experimental codegen ceiling.",
                "receive rows isolate repeated IRpcCodec<T>.Deserialize dispatch; hoisted intentionally has the same receive shape as interface because there is no stream-invariant sized capability to move.",
                "Each measurement performs three warmup loops before measured rounds so Dynamic PGO/OSR has an opportunity to optimize the hot generic or interface loop.",
                "The runner is intentionally benchmark-only and does not change SharpLink public ABI, Codec registry shape, generated ABI, or production stream semantics."
            ]
        };

        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        await File.WriteAllTextAsync(outputPath, json).ConfigureAwait(false);
        Console.WriteLine(json);
    }

    private static void AddExactClassMeasurements(
        List<StreamCodecEvidenceMeasurement> measurements,
        StreamCodecEvidenceMode mode,
        int rounds,
        int itemsPerRound)
    {
        measurements.Add(MeasureExact(
            "send/int32/exact-class", "send", "int32", "exact-class",
            0x12345678, new BlitClassCodec<int>(), mode, rounds, itemsPerRound));
        measurements.Add(MeasureExact(
            "send/int64/exact-class", "send", "int64", "exact-class",
            0x1020304050607080L, new BlitClassCodec<long>(), mode, rounds, itemsPerRound));
        measurements.Add(MeasureExact(
            "send/guid/exact-class", "send", "guid", "exact-class",
            new Guid("00112233-4455-6677-8899-aabbccddeeff"), new BlitClassCodec<Guid>(), mode, rounds, itemsPerRound));
        measurements.Add(MeasureExact(
            "send/16b/exact-class", "send", "16b", "exact-class",
            CodecPayload16.Create(), new BlitClassCodec<CodecPayload16>(), mode, rounds, itemsPerRound));
        measurements.Add(MeasureExact(
            "send/64b/exact-class", "send", "64b", "exact-class",
            CodecPayload64.Create(), new BlitClassCodec<CodecPayload64>(), mode, rounds, itemsPerRound));
        measurements.Add(MeasureExact(
            "send/256b/exact-class", "send", "256b", "exact-class",
            CodecPayload256.Create(), new BlitClassCodec<CodecPayload256>(), mode, rounds, itemsPerRound));
    }

    private static void AddUnsizedClassMeasurements(
        List<StreamCodecEvidenceMeasurement> measurements,
        StreamCodecEvidenceMode mode,
        int rounds,
        int itemsPerRound)
    {
        measurements.Add(MeasureUnsized(
            "send/int32/unsized-class", "send", "int32", "unsized-class",
            0x12345678, new BlitUnsizedClassCodec<int>(), mode, rounds, itemsPerRound));
        measurements.Add(MeasureUnsized(
            "send/16b/unsized-class", "send", "16b", "unsized-class",
            CodecPayload16.Create(), new BlitUnsizedClassCodec<CodecPayload16>(), mode, rounds, itemsPerRound));
        measurements.Add(MeasureUnsized(
            "send/64b/unsized-class", "send", "64b", "unsized-class",
            CodecPayload64.Create(), new BlitUnsizedClassCodec<CodecPayload64>(), mode, rounds, itemsPerRound));
    }

    private static void AddExactStructMeasurements(
        List<StreamCodecEvidenceMeasurement> measurements,
        StreamCodecEvidenceMode mode,
        int rounds,
        int itemsPerRound)
    {
        measurements.Add(MeasureExact(
            "send/int32/exact-struct", "send", "int32", "exact-struct",
            0x12345678, new BlitStructCodec<int>(), mode, rounds, itemsPerRound));
        measurements.Add(MeasureExact(
            "send/16b/exact-struct", "send", "16b", "exact-struct",
            CodecPayload16.Create(), new BlitStructCodec<CodecPayload16>(), mode, rounds, itemsPerRound));
    }

    private static void AddReceiveMeasurements(
        List<StreamCodecEvidenceMeasurement> measurements,
        StreamCodecEvidenceMode mode,
        int rounds,
        int itemsPerRound)
    {
        measurements.Add(MeasureReceive(
            "receive/int32/exact-class", "int32", "exact-class",
            0x12345678, new BlitClassCodec<int>(), mode, rounds, itemsPerRound));
        measurements.Add(MeasureReceive(
            "receive/guid/exact-class", "guid", "exact-class",
            new Guid("00112233-4455-6677-8899-aabbccddeeff"), new BlitClassCodec<Guid>(), mode, rounds, itemsPerRound));
        measurements.Add(MeasureReceive(
            "receive/16b/exact-class", "16b", "exact-class",
            CodecPayload16.Create(), new BlitClassCodec<CodecPayload16>(), mode, rounds, itemsPerRound));
        measurements.Add(MeasureReceive(
            "receive/64b/exact-class", "64b", "exact-class",
            CodecPayload64.Create(), new BlitClassCodec<CodecPayload64>(), mode, rounds, itemsPerRound));
        measurements.Add(MeasureReceive(
            "receive/int32/unsized-class", "int32", "unsized-class",
            0x12345678, new BlitUnsizedClassCodec<int>(), mode, rounds, itemsPerRound));
        measurements.Add(MeasureReceive(
            "receive/int32/exact-struct", "int32", "exact-struct",
            0x12345678, new BlitStructCodec<int>(), mode, rounds, itemsPerRound));
    }

    private static StreamCodecEvidenceMeasurement MeasureExact<T, TCodec>(
        string scenario,
        string direction,
        string payload,
        string codecShape,
        T value,
        TCodec codec,
        StreamCodecEvidenceMode mode,
        int rounds,
        int itemsPerRound)
        where T : unmanaged
        where TCodec : IRpcCodec<T>, IRpcSizedCodec<T>
    {
        IRpcCodec<T> erased = codec;
        using var writer = new PooledByteBufferWriter(Unsafe.SizeOf<T>() + 32);
        long Execute(int count) => mode switch
        {
            StreamCodecEvidenceMode.Interface => RunCurrentInterfaceSend(erased, value, writer, count),
            StreamCodecEvidenceMode.Hoisted => RunHoistedInterfaceSend(erased, value, writer, count),
            StreamCodecEvidenceMode.Specialized => RunSpecializedSend<T, TCodec>(codec, value, writer, count),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };

        return Measure(
            scenario,
            direction,
            payload,
            codecShape,
            Unsafe.SizeOf<T>(),
            rounds,
            itemsPerRound,
            Execute,
            expectedChecksumPerItem: Unsafe.SizeOf<T>());
    }

    private static StreamCodecEvidenceMeasurement MeasureUnsized<T, TCodec>(
        string scenario,
        string direction,
        string payload,
        string codecShape,
        T value,
        TCodec codec,
        StreamCodecEvidenceMode mode,
        int rounds,
        int itemsPerRound)
        where T : unmanaged
        where TCodec : IRpcCodec<T>
    {
        IRpcCodec<T> erased = codec;
        using var writer = new PooledByteBufferWriter(Unsafe.SizeOf<T>() + 32);
        long Execute(int count) => mode switch
        {
            StreamCodecEvidenceMode.Interface => RunCurrentInterfaceSend(erased, value, writer, count),
            StreamCodecEvidenceMode.Hoisted => RunHoistedInterfaceSend(erased, value, writer, count),
            StreamCodecEvidenceMode.Specialized => RunSpecializedUnsizedSend<T, TCodec>(codec, value, writer, count),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };

        return Measure(
            scenario,
            direction,
            payload,
            codecShape,
            Unsafe.SizeOf<T>(),
            rounds,
            itemsPerRound,
            Execute,
            expectedChecksumPerItem: Unsafe.SizeOf<T>());
    }

    private static StreamCodecEvidenceMeasurement MeasureReceive<T, TCodec>(
        string scenario,
        string payload,
        string codecShape,
        T value,
        TCodec codec,
        StreamCodecEvidenceMode mode,
        int rounds,
        int itemsPerRound)
        where T : unmanaged
        where TCodec : IRpcCodec<T>
    {
        IRpcCodec<T> erased = codec;
        var bytes = new byte[Unsafe.SizeOf<T>()];
        MemoryMarshal.Write(bytes.AsSpan(), in value);
        var sequence = new ReadOnlySequence<byte>(bytes);
        var expectedChecksumPerItem = bytes[0];
        if (expectedChecksumPerItem == 0)
            throw new InvalidOperationException($"Receive evidence payload {scenario} must have a non-zero first byte.");

        long Execute(int count) => mode switch
        {
            StreamCodecEvidenceMode.Interface => RunInterfaceReceive(erased, sequence, count),
            StreamCodecEvidenceMode.Hoisted => RunInterfaceReceive(erased, sequence, count),
            StreamCodecEvidenceMode.Specialized => RunSpecializedReceive<T, TCodec>(codec, sequence, count),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };

        return Measure(
            scenario,
            "receive",
            payload,
            codecShape,
            Unsafe.SizeOf<T>(),
            rounds,
            itemsPerRound,
            Execute,
            expectedChecksumPerItem);
    }

    private static StreamCodecEvidenceMeasurement Measure(
        string scenario,
        string direction,
        string payload,
        string codecShape,
        int payloadBytes,
        int rounds,
        int itemsPerRound,
        Func<int, long> execute,
        long expectedChecksumPerItem)
    {
        var warmupCount = Math.Min(itemsPerRound, WarmupItems);
        for (var warmup = 0; warmup < 3; warmup++)
            ValidateChecksum(scenario, execute(warmupCount), expectedChecksumPerItem, warmupCount);

        var samples = new List<StreamCodecEvidenceRound>(rounds);
        using var process = Process.GetCurrentProcess();
        for (var round = 0; round < rounds; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            process.Refresh();
            var cpuBefore = process.TotalProcessorTime;
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            var checksum = execute(itemsPerRound);
            var finished = Stopwatch.GetTimestamp();
            var allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
            process.Refresh();
            var cpuAfter = process.TotalProcessorTime;

            ValidateChecksum(scenario, checksum, expectedChecksumPerItem, itemsPerRound);
            var elapsedSeconds = (finished - started) / (double)Stopwatch.Frequency;
            samples.Add(new StreamCodecEvidenceRound
            {
                Round = round + 1,
                NanosecondsPerItem = elapsedSeconds * 1_000_000_000d / itemsPerRound,
                ItemsPerSecond = itemsPerRound / Math.Max(elapsedSeconds, double.Epsilon),
                CpuNanosecondsPerItem = (cpuAfter - cpuBefore).TotalMilliseconds * 1_000_000d / itemsPerRound,
                AllocatedBytesPerItem = (allocatedAfter - allocatedBefore) / (double)itemsPerRound,
                Checksum = checksum
            });
        }

        return new StreamCodecEvidenceMeasurement
        {
            Scenario = scenario,
            Direction = direction,
            Payload = payload,
            PayloadBytes = payloadBytes,
            CodecShape = codecShape,
            NanosecondsPerItem = Median(samples.Select(static row => row.NanosecondsPerItem)),
            ItemsPerSecond = Median(samples.Select(static row => row.ItemsPerSecond)),
            CpuNanosecondsPerItem = Median(samples.Select(static row => row.CpuNanosecondsPerItem)),
            AllocatedBytesPerItem = Median(samples.Select(static row => row.AllocatedBytesPerItem)),
            RoundMeasurements = samples
        };
    }

    private static void ValidateChecksum(
        string scenario,
        long actual,
        long expectedPerItem,
        int itemCount)
    {
        var expected = checked(expectedPerItem * itemCount);
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"Stream Codec evidence scenario {scenario} produced checksum {actual} instead of {expected}.");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RunCurrentInterfaceSend<T>(
        IRpcCodec<T> codec,
        T value,
        PooledByteBufferWriter writer,
        int items)
    {
        long checksum = 0;
        for (var index = 0; index < items; index++)
        {
            writer.Clear();
            if (codec is IRpcSizedCodec<T> sizedCodec &&
                sizedCodec.CanExactSize &&
                sizedCodec.TryGetEncodedSize(value, out var size, out var snapshot))
            {
                try
                {
                    sizedCodec.SerializeSized(value, writer, size, snapshot);
                }
                finally
                {
                    if (snapshot is not null)
                        sizedCodec.ReleaseSnapshot(snapshot);
                }
            }
            else
            {
                codec.Serialize(value, writer);
            }
            checksum += writer.WrittenCount;
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RunHoistedInterfaceSend<T>(
        IRpcCodec<T> codec,
        T value,
        PooledByteBufferWriter writer,
        int items)
    {
        if (codec is IRpcSizedCodec<T> sizedCodec && sizedCodec.CanExactSize)
            return RunHoistedSizedInterfaceSend(codec, sizedCodec, value, writer, items);
        return RunHoistedUnsizedInterfaceSend(codec, value, writer, items);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RunHoistedSizedInterfaceSend<T>(
        IRpcCodec<T> codec,
        IRpcSizedCodec<T> sizedCodec,
        T value,
        PooledByteBufferWriter writer,
        int items)
    {
        long checksum = 0;
        for (var index = 0; index < items; index++)
        {
            writer.Clear();
            if (sizedCodec.TryGetEncodedSize(value, out var size, out var snapshot))
            {
                try
                {
                    sizedCodec.SerializeSized(value, writer, size, snapshot);
                }
                finally
                {
                    if (snapshot is not null)
                        sizedCodec.ReleaseSnapshot(snapshot);
                }
            }
            else
            {
                codec.Serialize(value, writer);
            }
            checksum += writer.WrittenCount;
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RunHoistedUnsizedInterfaceSend<T>(
        IRpcCodec<T> codec,
        T value,
        PooledByteBufferWriter writer,
        int items)
    {
        long checksum = 0;
        for (var index = 0; index < items; index++)
        {
            writer.Clear();
            codec.Serialize(value, writer);
            checksum += writer.WrittenCount;
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RunSpecializedSend<T, TCodec>(
        TCodec codec,
        T value,
        PooledByteBufferWriter writer,
        int items)
        where TCodec : IRpcCodec<T>, IRpcSizedCodec<T>
    {
        if (!codec.CanExactSize)
            return RunSpecializedUnsizedSend<T, TCodec>(codec, value, writer, items);

        long checksum = 0;
        for (var index = 0; index < items; index++)
        {
            writer.Clear();
            if (codec.TryGetEncodedSize(value, out var size, out var snapshot))
            {
                try
                {
                    codec.SerializeSized(value, writer, size, snapshot);
                }
                finally
                {
                    if (snapshot is not null)
                        codec.ReleaseSnapshot(snapshot);
                }
            }
            else
            {
                codec.Serialize(value, writer);
            }
            checksum += writer.WrittenCount;
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RunSpecializedUnsizedSend<T, TCodec>(
        TCodec codec,
        T value,
        PooledByteBufferWriter writer,
        int items)
        where TCodec : IRpcCodec<T>
    {
        long checksum = 0;
        for (var index = 0; index < items; index++)
        {
            writer.Clear();
            codec.Serialize(value, writer);
            checksum += writer.WrittenCount;
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RunInterfaceReceive<T>(
        IRpcCodec<T> codec,
        ReadOnlySequence<byte> payload,
        int items)
        where T : unmanaged
    {
        long checksum = 0;
        for (var index = 0; index < items; index++)
        {
            var value = codec.Deserialize(payload);
            checksum += FirstByte(value);
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RunSpecializedReceive<T, TCodec>(
        TCodec codec,
        ReadOnlySequence<byte> payload,
        int items)
        where T : unmanaged
        where TCodec : IRpcCodec<T>
    {
        long checksum = 0;
        for (var index = 0; index < items; index++)
        {
            var value = codec.Deserialize(payload);
            checksum += FirstByte(value);
        }
        return checksum;
    }

    private static byte FirstByte<T>(T value)
        where T : unmanaged
    {
        var span = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1));
        return span[0];
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(static value => value).ToArray();
        if (ordered.Length == 0)
            throw new InvalidOperationException("Cannot calculate a median without samples.");
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }

    private static string ReadEnvironment(string name)
        => Environment.GetEnvironmentVariable(name) ?? "default";
}

internal enum StreamCodecEvidenceMode
{
    Interface,
    Hoisted,
    Specialized
}

internal sealed class StreamCodecEvidenceDocument
{
    public string Configuration { get; init; } = string.Empty;
    public int Trial { get; init; }
    public string Mode { get; init; } = string.Empty;
    public string Commit { get; init; } = string.Empty;
    public string Framework { get; init; } = string.Empty;
    public string Os { get; init; } = string.Empty;
    public string Architecture { get; init; } = string.Empty;
    public int ProcessorCount { get; init; }
    public string TieredCompilation { get; init; } = string.Empty;
    public string TieredPgo { get; init; } = string.Empty;
    public string ReadyToRun { get; init; } = string.Empty;
    public string QuickJitForLoops { get; init; } = string.Empty;
    public string OnStackReplacement { get; init; } = string.Empty;
    public int Rounds { get; init; }
    public int ItemsPerRound { get; init; }
    public IReadOnlyList<StreamCodecEvidenceMeasurement> Measurements { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
}

internal sealed class StreamCodecEvidenceMeasurement
{
    public string Scenario { get; init; } = string.Empty;
    public string Direction { get; init; } = string.Empty;
    public string Payload { get; init; } = string.Empty;
    public int PayloadBytes { get; init; }
    public string CodecShape { get; init; } = string.Empty;
    public double NanosecondsPerItem { get; init; }
    public double ItemsPerSecond { get; init; }
    public double CpuNanosecondsPerItem { get; init; }
    public double AllocatedBytesPerItem { get; init; }
    public IReadOnlyList<StreamCodecEvidenceRound> RoundMeasurements { get; init; } = [];
}

internal sealed class StreamCodecEvidenceRound
{
    public int Round { get; init; }
    public double NanosecondsPerItem { get; init; }
    public double ItemsPerSecond { get; init; }
    public double CpuNanosecondsPerItem { get; init; }
    public double AllocatedBytesPerItem { get; init; }
    public long Checksum { get; init; }
}

internal sealed class BlitClassCodec<T> : IRpcCodec<T>, IRpcSizedCodec<T>
    where T : unmanaged
{
    public bool CanExactSize => true;

    public void Serialize(in T value, IBufferWriter<byte> buffer)
        => BlitCodecWire<T>.Serialize(value, buffer);

    public T Deserialize(in ReadOnlySequence<byte> buffer)
        => BlitCodecWire<T>.Deserialize(buffer);

    public bool TryGetEncodedSize(in T value, out int size)
    {
        size = Unsafe.SizeOf<T>();
        return true;
    }

    public bool TryGetEncodedSize(
        in T value,
        out int size,
        out IRpcSizedCodecSnapshot? snapshot)
    {
        size = Unsafe.SizeOf<T>();
        snapshot = null;
        return true;
    }

    public void SerializeSized(
        in T value,
        IBufferWriter<byte> buffer,
        int size,
        IRpcSizedCodecSnapshot? snapshot)
    {
        if (size != Unsafe.SizeOf<T>() || snapshot is not null)
            throw new InvalidOperationException("Exact-size class Codec received an invalid benchmark size contract.");
        BlitCodecWire<T>.Serialize(value, buffer);
    }

    public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
    {
        if (snapshot is not null)
            throw new InvalidOperationException("Exact-size class Codec does not own snapshots.");
    }
}

internal readonly struct BlitStructCodec<T> : IRpcCodec<T>, IRpcSizedCodec<T>
    where T : unmanaged
{
    public bool CanExactSize => true;

    public void Serialize(in T value, IBufferWriter<byte> buffer)
        => BlitCodecWire<T>.Serialize(value, buffer);

    public T Deserialize(in ReadOnlySequence<byte> buffer)
        => BlitCodecWire<T>.Deserialize(buffer);

    public bool TryGetEncodedSize(in T value, out int size)
    {
        size = Unsafe.SizeOf<T>();
        return true;
    }

    public bool TryGetEncodedSize(
        in T value,
        out int size,
        out IRpcSizedCodecSnapshot? snapshot)
    {
        size = Unsafe.SizeOf<T>();
        snapshot = null;
        return true;
    }

    public void SerializeSized(
        in T value,
        IBufferWriter<byte> buffer,
        int size,
        IRpcSizedCodecSnapshot? snapshot)
    {
        if (size != Unsafe.SizeOf<T>() || snapshot is not null)
            throw new InvalidOperationException("Exact-size struct Codec received an invalid benchmark size contract.");
        BlitCodecWire<T>.Serialize(value, buffer);
    }

    public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
    {
        if (snapshot is not null)
            throw new InvalidOperationException("Exact-size struct Codec does not own snapshots.");
    }
}

internal sealed class BlitUnsizedClassCodec<T> : IRpcCodec<T>
    where T : unmanaged
{
    public void Serialize(in T value, IBufferWriter<byte> buffer)
        => BlitCodecWire<T>.Serialize(value, buffer);

    public T Deserialize(in ReadOnlySequence<byte> buffer)
        => BlitCodecWire<T>.Deserialize(buffer);
}

internal static class BlitCodecWire<T>
    where T : unmanaged
{
    internal static void Serialize(in T value, IBufferWriter<byte> buffer)
    {
        var size = Unsafe.SizeOf<T>();
        var destination = buffer.GetSpan(size);
        MemoryMarshal.Write(destination, in value);
        buffer.Advance(size);
    }

    internal static T Deserialize(in ReadOnlySequence<byte> buffer)
    {
        var size = Unsafe.SizeOf<T>();
        if (buffer.Length < size)
            throw new InvalidOperationException("Benchmark Codec payload is shorter than the unmanaged value.");
        if (buffer.IsSingleSegment)
            return MemoryMarshal.Read<T>(buffer.FirstSpan[..size]);

        Span<byte> temporary = stackalloc byte[size];
        buffer.Slice(0, size).CopyTo(temporary);
        return MemoryMarshal.Read<T>(temporary);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct CodecPayload16(long A, long B)
{
    internal static CodecPayload16 Create() => new(0x0102030405060708L, 0x1112131415161718L);
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct CodecPayload64(
    long A,
    long B,
    long C,
    long D,
    long E,
    long F,
    long G,
    long H)
{
    internal static CodecPayload64 Create()
        => new(
            0x0102030405060708L,
            0x1112131415161718L,
            0x2122232425262728L,
            0x3132333435363738L,
            0x4142434445464748L,
            0x5152535455565758L,
            0x6162636465666768L,
            0x7172737475767778L);
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct CodecPayload256(
    CodecPayload64 A,
    CodecPayload64 B,
    CodecPayload64 C,
    CodecPayload64 D)
{
    internal static CodecPayload256 Create()
    {
        var payload = CodecPayload64.Create();
        return new(payload, payload, payload, payload);
    }
}
