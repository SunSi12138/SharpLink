using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using SharpLink.Abstractions;

namespace SharpLink.Benchmarks;

/// <summary>
/// Issue #720 micro-evidence for stream-level codec capability hoisting, Dynamic PGO
/// devirtualization, and concrete TCodec specialization.
/// </summary>
internal static class StreamCodecDispatchEvidenceRunner
{
    private const int Rounds = 5;
    private const int TargetItemsPerRound = 4_000_000;
    private const int WarmupItems = 1_000_000;
    private static readonly int[] StreamLengths = [1_000, 10_000, 100_000];

    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 2)
        {
            throw new ArgumentException(
                "Usage: --stream-codec-dispatch-evidence " +
                "<interface-per-item|interface-hoisted|generic-class|generic-struct> <output-json>");
        }

        var shape = args[0];
        if (shape is not ("interface-per-item" or "interface-hoisted" or "generic-class" or "generic-struct"))
            throw new ArgumentOutOfRangeException(nameof(args), shape, "Unknown stream codec dispatch shape.");

        var outputPath = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var measurements = new List<StreamCodecDispatchMeasurement>();
        AddPayloadMeasurements("int", CreateValue<int>(), shape, measurements);
        AddPayloadMeasurements("guid", CreateValue<Guid>(), shape, measurements);
        AddPayloadMeasurements("payload16", CreateValue<Payload16>(), shape, measurements);
        AddPayloadMeasurements("payload64", CreateValue<Payload64>(), shape, measurements);

        var document = new StreamCodecDispatchEvidenceDocument
        {
            Commit = Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA") ?? "unknown",
            Shape = shape,
            Framework = RuntimeInformation.FrameworkDescription,
            Os = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            ProcessorCount = Environment.ProcessorCount,
            TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default",
            TieredPgo = Environment.GetEnvironmentVariable("DOTNET_TieredPGO") ?? "default",
            ReadyToRun = Environment.GetEnvironmentVariable("DOTNET_ReadyToRun") ?? "default",
            Rounds = Rounds,
            Measurements = measurements,
            Notes =
            [
                "This is dispatch micro-evidence, not an end-to-end RPC throughput claim.",
                "interface-per-item models the current send shape: IRpcSizedCodec<T> type test and CanExactSize are repeated for every item.",
                "interface-hoisted performs the IRpcSizedCodec<T> capability test once per stream while retaining per-item TryGetEncodedSize.",
                "generic-class and generic-struct model an already-opened stream-level TCodec specialization; provider/registry existential unpacking is intentionally outside the timed loop.",
                "The deserialize scenario isolates the long-lived IRpcCodec<T>.Deserialize dispatch used by PooledAsyncStreamDispatcher<T>.",
                "Each result is the median of five rounds after a stream-length-aware warmup. All shapes use the same scratch writer and unmanaged codec behavior."
            ]
        };

        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        await File.WriteAllTextAsync(outputPath, json).ConfigureAwait(false);
        Console.WriteLine(
            "Wrote stream codec dispatch evidence for " + shape + " to " + outputPath + ".");
    }

    public static void RunJitProbe(string[] args)
    {
        if (args.Length != 2)
        {
            throw new ArgumentException(
                "Usage: --stream-codec-dispatch-jit-evidence " +
                "<interface-per-item|interface-hoisted|generic-class|generic-struct> <stream-count>");
        }

        var shape = args[0];
        var streamCount = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(streamCount);

        var value = CreateValue<int>();
        var writer = new ScratchBufferWriter();
        var payload = CreatePayload(in value);
        long checksum;

        switch (shape)
        {
            case "interface-per-item":
            {
                IRpcCodec<int> codec = new UnmanagedSizedClassCodec<int>();
                checksum = RunInterfacePerItemStreams(codec, in value, writer, 10_000, streamCount);
                checksum += RunInterfaceDeserializeStreams(codec, in payload, 10_000, streamCount);
                break;
            }
            case "interface-hoisted":
            {
                IRpcCodec<int> codec = new UnmanagedSizedClassCodec<int>();
                checksum = RunInterfaceHoistedStreams(codec, in value, writer, 10_000, streamCount);
                checksum += RunInterfaceDeserializeStreams(codec, in payload, 10_000, streamCount);
                break;
            }
            case "generic-class":
            {
                var codec = new UnmanagedSizedClassCodec<int>();
                checksum = RunGenericSizedStreams<int, UnmanagedSizedClassCodec<int>>(
                    codec, in value, writer, 10_000, streamCount);
                checksum += RunGenericDeserializeStreams<int, UnmanagedSizedClassCodec<int>>(
                    codec, in payload, 10_000, streamCount);
                break;
            }
            case "generic-struct":
            {
                var codec = new UnmanagedSizedStructCodec<int>();
                checksum = RunGenericSizedStreams<int, UnmanagedSizedStructCodec<int>>(
                    codec, in value, writer, 10_000, streamCount);
                checksum += RunGenericDeserializeStreams<int, UnmanagedSizedStructCodec<int>>(
                    codec, in payload, 10_000, streamCount);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(args), shape, "Unknown JIT evidence shape.");
        }

        Console.WriteLine("stream-codec-jit-probe checksum=" + checksum);
    }

    private static void AddPayloadMeasurements<T>(
        string payloadName,
        T value,
        string shape,
        List<StreamCodecDispatchMeasurement> measurements)
        where T : unmanaged
    {
        foreach (var streamLength in StreamLengths)
        {
            switch (shape)
            {
                case "interface-per-item":
                    AddInterfacePerItemMeasurements(payloadName, in value, streamLength, measurements);
                    break;
                case "interface-hoisted":
                    AddInterfaceHoistedMeasurements(payloadName, in value, streamLength, measurements);
                    break;
                case "generic-class":
                    AddGenericClassMeasurements(payloadName, in value, streamLength, measurements);
                    break;
                case "generic-struct":
                    AddGenericStructMeasurements(payloadName, in value, streamLength, measurements);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
            }
        }
    }

    private static void AddInterfacePerItemMeasurements<T>(
        string payloadName,
        in T value,
        int streamLength,
        List<StreamCodecDispatchMeasurement> measurements)
        where T : unmanaged
    {
        IRpcCodec<T> sizedCodec = new UnmanagedSizedClassCodec<T>();
        IRpcCodec<T> unsizedCodec = new UnmanagedClassCodec<T>();
        var writer = new ScratchBufferWriter();
        var payload = CreatePayload(in value);

        measurements.Add(Measure(
            payloadName,
            "sized-serialize",
            streamLength,
            count => RunInterfacePerItemStreams(sizedCodec, in value, writer, streamLength, count)));
        measurements.Add(Measure(
            payloadName,
            "unsized-serialize",
            streamLength,
            count => RunInterfacePerItemStreams(unsizedCodec, in value, writer, streamLength, count)));
        measurements.Add(Measure(
            payloadName,
            "deserialize",
            streamLength,
            count => RunInterfaceDeserializeStreams(sizedCodec, in payload, streamLength, count)));
    }

    private static void AddInterfaceHoistedMeasurements<T>(
        string payloadName,
        in T value,
        int streamLength,
        List<StreamCodecDispatchMeasurement> measurements)
        where T : unmanaged
    {
        IRpcCodec<T> sizedCodec = new UnmanagedSizedClassCodec<T>();
        IRpcCodec<T> unsizedCodec = new UnmanagedClassCodec<T>();
        var writer = new ScratchBufferWriter();
        var payload = CreatePayload(in value);

        measurements.Add(Measure(
            payloadName,
            "sized-serialize",
            streamLength,
            count => RunInterfaceHoistedStreams(sizedCodec, in value, writer, streamLength, count)));
        measurements.Add(Measure(
            payloadName,
            "unsized-serialize",
            streamLength,
            count => RunInterfaceHoistedStreams(unsizedCodec, in value, writer, streamLength, count)));
        measurements.Add(Measure(
            payloadName,
            "deserialize",
            streamLength,
            count => RunInterfaceDeserializeStreams(sizedCodec, in payload, streamLength, count)));
    }

    private static void AddGenericClassMeasurements<T>(
        string payloadName,
        in T value,
        int streamLength,
        List<StreamCodecDispatchMeasurement> measurements)
        where T : unmanaged
    {
        var sizedCodec = new UnmanagedSizedClassCodec<T>();
        var unsizedCodec = new UnmanagedClassCodec<T>();
        var writer = new ScratchBufferWriter();
        var payload = CreatePayload(in value);

        measurements.Add(Measure(
            payloadName,
            "sized-serialize",
            streamLength,
            count => RunGenericSizedStreams<T, UnmanagedSizedClassCodec<T>>(
                sizedCodec, in value, writer, streamLength, count)));
        measurements.Add(Measure(
            payloadName,
            "unsized-serialize",
            streamLength,
            count => RunGenericUnsizedStreams<T, UnmanagedClassCodec<T>>(
                unsizedCodec, in value, writer, streamLength, count)));
        measurements.Add(Measure(
            payloadName,
            "deserialize",
            streamLength,
            count => RunGenericDeserializeStreams<T, UnmanagedSizedClassCodec<T>>(
                sizedCodec, in payload, streamLength, count)));
    }

    private static void AddGenericStructMeasurements<T>(
        string payloadName,
        in T value,
        int streamLength,
        List<StreamCodecDispatchMeasurement> measurements)
        where T : unmanaged
    {
        var sizedCodec = new UnmanagedSizedStructCodec<T>();
        var unsizedCodec = new UnmanagedStructCodec<T>();
        var writer = new ScratchBufferWriter();
        var payload = CreatePayload(in value);

        measurements.Add(Measure(
            payloadName,
            "sized-serialize",
            streamLength,
            count => RunGenericSizedStreams<T, UnmanagedSizedStructCodec<T>>(
                sizedCodec, in value, writer, streamLength, count)));
        measurements.Add(Measure(
            payloadName,
            "unsized-serialize",
            streamLength,
            count => RunGenericUnsizedStreams<T, UnmanagedStructCodec<T>>(
                unsizedCodec, in value, writer, streamLength, count)));
        measurements.Add(Measure(
            payloadName,
            "deserialize",
            streamLength,
            count => RunGenericDeserializeStreams<T, UnmanagedSizedStructCodec<T>>(
                sizedCodec, in payload, streamLength, count)));
    }

    private static StreamCodecDispatchMeasurement Measure(
        string payloadName,
        string operation,
        int streamLength,
        Func<int, long> run)
    {
        var streamsPerRound = Math.Max(1, DivideRoundUp(TargetItemsPerRound, streamLength));
        var warmupStreams = Math.Max(40, DivideRoundUp(WarmupItems, streamLength));
        _ = run(warmupStreams);

        var wallSamples = new double[Rounds];
        var cpuSamples = new double[Rounds];
        var allocationSamples = new double[Rounds];
        long checksum = 0;
        using var process = Process.GetCurrentProcess();

        for (var round = 0; round < Rounds; round++)
        {
            var itemCount = checked((long)streamLength * streamsPerRound);
            process.Refresh();
            var cpuBefore = process.TotalProcessorTime;
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();

            checksum ^= run(streamsPerRound);

            var elapsedTicks = Stopwatch.GetTimestamp() - started;
            var allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
            process.Refresh();
            var cpuAfter = process.TotalProcessorTime;

            wallSamples[round] = elapsedTicks * 1_000_000_000d / Stopwatch.Frequency / itemCount;
            cpuSamples[round] = (cpuAfter - cpuBefore).TotalSeconds * 1_000_000_000d / itemCount;
            allocationSamples[round] = (allocatedAfter - allocatedBefore) / (double)itemCount;
        }

        if (checksum == long.MinValue)
            throw new InvalidOperationException("Impossible checksum sentinel observed.");

        return new StreamCodecDispatchMeasurement
        {
            Payload = payloadName,
            PayloadBytes = PayloadSize(payloadName),
            Operation = operation,
            StreamLength = streamLength,
            StreamsPerRound = streamsPerRound,
            NanosecondsPerItem = Median(wallSamples),
            CpuNanosecondsPerItem = Median(cpuSamples),
            AllocatedBytesPerItem = Median(allocationSamples),
            Checksum = checksum
        };
    }

    private static int PayloadSize(string payloadName)
        => payloadName switch
        {
            "int" => sizeof(int),
            "guid" => 16,
            "payload16" => 16,
            "payload64" => 64,
            _ => throw new ArgumentOutOfRangeException(nameof(payloadName), payloadName, null)
        };

    private static int DivideRoundUp(int value, int divisor)
        => checked((value + divisor - 1) / divisor);

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(static value => value).ToArray();
        var middle = ordered.Length / 2;
        return (ordered.Length & 1) == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }

    private static T CreateValue<T>()
        where T : unmanaged
    {
        Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<T>()];
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] = unchecked((byte)(0x2a + index));
        return MemoryMarshal.Read<T>(bytes);
    }

    private static ReadOnlySequence<byte> CreatePayload<T>(in T value)
        where T : unmanaged
    {
        var bytes = new byte[Unsafe.SizeOf<T>()];
        MemoryMarshal.Write(bytes.AsSpan(), in value);
        return new ReadOnlySequence<byte>(bytes);
    }

    private static long RunInterfacePerItemStreams<T>(
        IRpcCodec<T> codec,
        in T value,
        ScratchBufferWriter writer,
        int streamLength,
        int streamCount)
        where T : unmanaged
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
            checksum += StreamCodecEvidence_PumpInterfacePerItem(codec, in value, writer, streamLength);
        return checksum;
    }

    private static long RunInterfaceHoistedStreams<T>(
        IRpcCodec<T> codec,
        in T value,
        ScratchBufferWriter writer,
        int streamLength,
        int streamCount)
        where T : unmanaged
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
            checksum += StreamCodecEvidence_PumpInterfaceHoisted(codec, in value, writer, streamLength);
        return checksum;
    }

    private static long RunInterfaceDeserializeStreams<T>(
        IRpcCodec<T> codec,
        in ReadOnlySequence<byte> payload,
        int streamLength,
        int streamCount)
        where T : unmanaged
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
            checksum += StreamCodecEvidence_PumpInterfaceDeserialize(codec, in payload, streamLength);
        return checksum;
    }

    private static long RunGenericSizedStreams<T, TCodec>(
        TCodec codec,
        in T value,
        ScratchBufferWriter writer,
        int streamLength,
        int streamCount)
        where T : unmanaged
        where TCodec : IRpcCodec<T>, IRpcSizedCodec<T>
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
            checksum += StreamCodecEvidence_PumpGenericSized<T, TCodec>(
                codec, in value, writer, streamLength);
        return checksum;
    }

    private static long RunGenericUnsizedStreams<T, TCodec>(
        TCodec codec,
        in T value,
        ScratchBufferWriter writer,
        int streamLength,
        int streamCount)
        where T : unmanaged
        where TCodec : IRpcCodec<T>
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
            checksum += StreamCodecEvidence_PumpGenericUnsized<T, TCodec>(
                codec, in value, writer, streamLength);
        return checksum;
    }

    private static long RunGenericDeserializeStreams<T, TCodec>(
        TCodec codec,
        in ReadOnlySequence<byte> payload,
        int streamLength,
        int streamCount)
        where T : unmanaged
        where TCodec : IRpcCodec<T>
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
            checksum += StreamCodecEvidence_PumpGenericDeserialize<T, TCodec>(
                codec, in payload, streamLength);
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long StreamCodecEvidence_PumpInterfacePerItem<T>(
        IRpcCodec<T> codec,
        in T value,
        ScratchBufferWriter writer,
        int itemCount)
        where T : unmanaged
    {
        long checksum = 0;
        for (var index = 0; index < itemCount; index++)
        {
            writer.Reset();
            if (codec is IRpcSizedCodec<T> sizedCodec &&
                sizedCodec.CanExactSize &&
                sizedCodec.TryGetEncodedSize(in value, out var encodedBytes, out var snapshot))
            {
                try
                {
                    sizedCodec.SerializeSized(in value, writer, encodedBytes, snapshot);
                }
                finally
                {
                    if (snapshot is not null)
                        sizedCodec.ReleaseSnapshot(snapshot);
                }
            }
            else
            {
                codec.Serialize(in value, writer);
            }

            checksum += writer.WrittenSpan[0];
        }

        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long StreamCodecEvidence_PumpInterfaceHoisted<T>(
        IRpcCodec<T> codec,
        in T value,
        ScratchBufferWriter writer,
        int itemCount)
        where T : unmanaged
    {
        if (codec is not IRpcSizedCodec<T> sizedCodec || !sizedCodec.CanExactSize)
            return StreamCodecEvidence_PumpInterfaceSerializeOnly(codec, in value, writer, itemCount);

        long checksum = 0;
        for (var index = 0; index < itemCount; index++)
        {
            writer.Reset();
            if (sizedCodec.TryGetEncodedSize(in value, out var encodedBytes, out var snapshot))
            {
                try
                {
                    sizedCodec.SerializeSized(in value, writer, encodedBytes, snapshot);
                }
                finally
                {
                    if (snapshot is not null)
                        sizedCodec.ReleaseSnapshot(snapshot);
                }
            }
            else
            {
                codec.Serialize(in value, writer);
            }

            checksum += writer.WrittenSpan[0];
        }

        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long StreamCodecEvidence_PumpInterfaceSerializeOnly<T>(
        IRpcCodec<T> codec,
        in T value,
        ScratchBufferWriter writer,
        int itemCount)
        where T : unmanaged
    {
        long checksum = 0;
        for (var index = 0; index < itemCount; index++)
        {
            writer.Reset();
            codec.Serialize(in value, writer);
            checksum += writer.WrittenSpan[0];
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long StreamCodecEvidence_PumpInterfaceDeserialize<T>(
        IRpcCodec<T> codec,
        in ReadOnlySequence<byte> payload,
        int itemCount)
        where T : unmanaged
    {
        long checksum = 0;
        for (var index = 0; index < itemCount; index++)
        {
            var item = codec.Deserialize(in payload);
            checksum += Unsafe.As<T, byte>(ref item);
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long StreamCodecEvidence_PumpGenericSized<T, TCodec>(
        TCodec codec,
        in T value,
        ScratchBufferWriter writer,
        int itemCount)
        where T : unmanaged
        where TCodec : IRpcCodec<T>, IRpcSizedCodec<T>
    {
        if (!codec.CanExactSize)
            return StreamCodecEvidence_PumpGenericUnsized<T, TCodec>(
                codec, in value, writer, itemCount);

        long checksum = 0;
        for (var index = 0; index < itemCount; index++)
        {
            writer.Reset();
            if (codec.TryGetEncodedSize(in value, out var encodedBytes, out var snapshot))
            {
                try
                {
                    codec.SerializeSized(in value, writer, encodedBytes, snapshot);
                }
                finally
                {
                    if (snapshot is not null)
                        codec.ReleaseSnapshot(snapshot);
                }
            }
            else
            {
                codec.Serialize(in value, writer);
            }

            checksum += writer.WrittenSpan[0];
        }

        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long StreamCodecEvidence_PumpGenericUnsized<T, TCodec>(
        TCodec codec,
        in T value,
        ScratchBufferWriter writer,
        int itemCount)
        where T : unmanaged
        where TCodec : IRpcCodec<T>
    {
        long checksum = 0;
        for (var index = 0; index < itemCount; index++)
        {
            writer.Reset();
            codec.Serialize(in value, writer);
            checksum += writer.WrittenSpan[0];
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long StreamCodecEvidence_PumpGenericDeserialize<T, TCodec>(
        TCodec codec,
        in ReadOnlySequence<byte> payload,
        int itemCount)
        where T : unmanaged
        where TCodec : IRpcCodec<T>
    {
        long checksum = 0;
        for (var index = 0; index < itemCount; index++)
        {
            var item = codec.Deserialize(in payload);
            checksum += Unsafe.As<T, byte>(ref item);
        }
        return checksum;
    }

    private static class UnmanagedCodecOperations<T>
        where T : unmanaged
    {
        public static int Size => Unsafe.SizeOf<T>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize(in T value, IBufferWriter<byte> buffer)
        {
            var span = buffer.GetSpan(Size);
            MemoryMarshal.Write(span, in value);
            buffer.Advance(Size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Deserialize(in ReadOnlySequence<byte> buffer)
            => MemoryMarshal.Read<T>(buffer.FirstSpan);
    }

    private sealed class UnmanagedClassCodec<T> : IRpcCodec<T>
        where T : unmanaged
    {
        public void Serialize(in T value, IBufferWriter<byte> buffer)
            => UnmanagedCodecOperations<T>.Serialize(in value, buffer);

        public T Deserialize(in ReadOnlySequence<byte> buffer)
            => UnmanagedCodecOperations<T>.Deserialize(in buffer);
    }

    private sealed class UnmanagedSizedClassCodec<T> : IRpcCodec<T>, IRpcSizedCodec<T>
        where T : unmanaged
    {
        public bool CanExactSize => true;

        public void Serialize(in T value, IBufferWriter<byte> buffer)
            => UnmanagedCodecOperations<T>.Serialize(in value, buffer);

        public T Deserialize(in ReadOnlySequence<byte> buffer)
            => UnmanagedCodecOperations<T>.Deserialize(in buffer);

        public bool TryGetEncodedSize(in T value, out int size)
        {
            size = UnmanagedCodecOperations<T>.Size;
            return true;
        }

        public bool TryGetEncodedSize(
            in T value,
            out int size,
            out IRpcSizedCodecSnapshot? snapshot)
        {
            size = UnmanagedCodecOperations<T>.Size;
            snapshot = null;
            return true;
        }

        public void SerializeSized(
            in T value,
            IBufferWriter<byte> buffer,
            int size,
            IRpcSizedCodecSnapshot? snapshot)
            => UnmanagedCodecOperations<T>.Serialize(in value, buffer);

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
        {
        }
    }

    private readonly struct UnmanagedStructCodec<T> : IRpcCodec<T>
        where T : unmanaged
    {
        public void Serialize(in T value, IBufferWriter<byte> buffer)
            => UnmanagedCodecOperations<T>.Serialize(in value, buffer);

        public T Deserialize(in ReadOnlySequence<byte> buffer)
            => UnmanagedCodecOperations<T>.Deserialize(in buffer);
    }

    private readonly struct UnmanagedSizedStructCodec<T> : IRpcCodec<T>, IRpcSizedCodec<T>
        where T : unmanaged
    {
        public bool CanExactSize => true;

        public void Serialize(in T value, IBufferWriter<byte> buffer)
            => UnmanagedCodecOperations<T>.Serialize(in value, buffer);

        public T Deserialize(in ReadOnlySequence<byte> buffer)
            => UnmanagedCodecOperations<T>.Deserialize(in buffer);

        public bool TryGetEncodedSize(in T value, out int size)
        {
            size = UnmanagedCodecOperations<T>.Size;
            return true;
        }

        public bool TryGetEncodedSize(
            in T value,
            out int size,
            out IRpcSizedCodecSnapshot? snapshot)
        {
            size = UnmanagedCodecOperations<T>.Size;
            snapshot = null;
            return true;
        }

        public void SerializeSized(
            in T value,
            IBufferWriter<byte> buffer,
            int size,
            IRpcSizedCodecSnapshot? snapshot)
            => UnmanagedCodecOperations<T>.Serialize(in value, buffer);

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
        {
        }
    }

    private sealed class ScratchBufferWriter : IBufferWriter<byte>
    {
        private readonly byte[] _buffer = new byte[512];
        private int _written;

        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

        public void Reset() => _written = 0;

        public void Advance(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (_written > _buffer.Length - count)
                throw new InvalidOperationException("Scratch writer capacity exceeded.");
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsSpan(_written);
        }

        private void Ensure(int sizeHint)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            if (sizeHint > _buffer.Length - _written)
                throw new InvalidOperationException("Scratch writer capacity exceeded.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Payload16
    {
        public long A;
        public long B;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Payload64
    {
        public long A;
        public long B;
        public long C;
        public long D;
        public long E;
        public long F;
        public long G;
        public long H;
    }
}

internal sealed class StreamCodecDispatchEvidenceDocument
{
    public string Commit { get; init; } = string.Empty;
    public string Shape { get; init; } = string.Empty;
    public string Framework { get; init; } = string.Empty;
    public string Os { get; init; } = string.Empty;
    public string Architecture { get; init; } = string.Empty;
    public int ProcessorCount { get; init; }
    public string TieredCompilation { get; init; } = string.Empty;
    public string TieredPgo { get; init; } = string.Empty;
    public string ReadyToRun { get; init; } = string.Empty;
    public int Rounds { get; init; }
    public IReadOnlyList<StreamCodecDispatchMeasurement> Measurements { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
}

internal sealed class StreamCodecDispatchMeasurement
{
    public string Payload { get; init; } = string.Empty;
    public int PayloadBytes { get; init; }
    public string Operation { get; init; } = string.Empty;
    public int StreamLength { get; init; }
    public int StreamsPerRound { get; init; }
    public double NanosecondsPerItem { get; init; }
    public double CpuNanosecondsPerItem { get; init; }
    public double AllocatedBytesPerItem { get; init; }
    public long Checksum { get; init; }
}
