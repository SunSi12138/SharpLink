using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using SharpLink.Abstractions;

namespace SharpLink.StreamCodecAotEvidence;

public static class Program
{
    private const int Rounds = 5;
    private const int TargetItemsPerRound = 2_000_000;
    private const int WarmupItems = 500_000;
    private static readonly int[] StreamLengths = [1_000, 10_000, 100_000];

#if STREAM_CODEC_INTERFACE
    private const string Shape = "interface-per-item";
#elif STREAM_CODEC_HOISTED
    private const string Shape = "interface-hoisted";
#elif STREAM_CODEC_GENERIC_CLASS
    private const string Shape = "generic-class";
#elif STREAM_CODEC_GENERIC_STRUCT
    private const string Shape = "generic-struct";
#else
#error Exactly one stream codec evidence shape must be selected.
#endif

    public static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine(
                "Usage: SharpLink.StreamCodecAotEvidence <output-json>");
            return 2;
        }

        var outputPath = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var measurements = new List<Measurement>();
        AddPayloadMeasurements("int", CreateValue<int>(), measurements);
        AddPayloadMeasurements("long", CreateValue<long>(), measurements);
        AddPayloadMeasurements("guid", CreateValue<Guid>(), measurements);
        AddPayloadMeasurements("payload16", CreateValue<Payload16>(), measurements);
        AddPayloadMeasurements("payload64", CreateValue<Payload64>(), measurements);
        AddPayloadMeasurements("payload256", CreateValue<Payload256>(), measurements);

        WriteJson(outputPath, measurements);
        Console.WriteLine(
            $"Wrote NativeAOT stream codec evidence for {Shape} to {outputPath}.");
        return 0;
    }

    private static void AddPayloadMeasurements<T>(
        string payloadName,
        T value,
        List<Measurement> measurements)
        where T : unmanaged
    {
        foreach (var streamLength in StreamLengths)
        {
            measurements.Add(Measure(
                payloadName,
                Unsafe.SizeOf<T>(),
                "sized-serialize",
                streamLength,
                CreateSizedRun(value, streamLength)));
            measurements.Add(Measure(
                payloadName,
                Unsafe.SizeOf<T>(),
                "unsized-serialize",
                streamLength,
                CreateUnsizedRun(value, streamLength)));

            var payload = CreatePayload(in value);
            measurements.Add(Measure(
                payloadName,
                Unsafe.SizeOf<T>(),
                "deserialize",
                streamLength,
                CreateDeserializeRun<T>(payload, streamLength)));
        }
    }

    private static Func<int, long> CreateSizedRun<T>(T value, int streamLength)
        where T : unmanaged
    {
        var writer = new ScratchBufferWriter();
#if STREAM_CODEC_INTERFACE || STREAM_CODEC_HOISTED
        IRpcCodec<T> codec = new UnmanagedSizedClassCodec<T>();
#if STREAM_CODEC_INTERFACE
        return streamCount =>
            RunInterfacePerItemStreams(codec, value, writer, streamLength, streamCount);
#else
        return streamCount =>
            RunInterfaceHoistedStreams(codec, value, writer, streamLength, streamCount);
#endif
#elif STREAM_CODEC_GENERIC_CLASS
        var codec = new UnmanagedSizedClassCodec<T>();
        return streamCount =>
            RunGenericSizedStreams<T, UnmanagedSizedClassCodec<T>>(
                codec,
                value,
                writer,
                streamLength,
                streamCount);
#else
        var codec = new UnmanagedSizedStructCodec<T>();
        return streamCount =>
            RunGenericSizedStreams<T, UnmanagedSizedStructCodec<T>>(
                codec,
                value,
                writer,
                streamLength,
                streamCount);
#endif
    }

    private static Func<int, long> CreateUnsizedRun<T>(T value, int streamLength)
        where T : unmanaged
    {
        var writer = new ScratchBufferWriter();
#if STREAM_CODEC_INTERFACE || STREAM_CODEC_HOISTED
        IRpcCodec<T> codec = new UnmanagedClassCodec<T>();
#if STREAM_CODEC_INTERFACE
        return streamCount =>
            RunInterfacePerItemStreams(codec, value, writer, streamLength, streamCount);
#else
        return streamCount =>
            RunInterfaceHoistedStreams(codec, value, writer, streamLength, streamCount);
#endif
#elif STREAM_CODEC_GENERIC_CLASS
        var codec = new UnmanagedClassCodec<T>();
        return streamCount =>
            RunGenericUnsizedStreams<T, UnmanagedClassCodec<T>>(
                codec,
                value,
                writer,
                streamLength,
                streamCount);
#else
        var codec = new UnmanagedStructCodec<T>();
        return streamCount =>
            RunGenericUnsizedStreams<T, UnmanagedStructCodec<T>>(
                codec,
                value,
                writer,
                streamLength,
                streamCount);
#endif
    }

    private static Func<int, long> CreateDeserializeRun<T>(
        ReadOnlySequence<byte> payload,
        int streamLength)
        where T : unmanaged
    {
#if STREAM_CODEC_INTERFACE || STREAM_CODEC_HOISTED
        IRpcCodec<T> codec = new UnmanagedSizedClassCodec<T>();
        return streamCount =>
            RunInterfaceDeserializeStreams(codec, payload, streamLength, streamCount);
#elif STREAM_CODEC_GENERIC_CLASS
        var codec = new UnmanagedSizedClassCodec<T>();
        return streamCount =>
            RunGenericDeserializeStreams<T, UnmanagedSizedClassCodec<T>>(
                codec,
                payload,
                streamLength,
                streamCount);
#else
        var codec = new UnmanagedSizedStructCodec<T>();
        return streamCount =>
            RunGenericDeserializeStreams<T, UnmanagedSizedStructCodec<T>>(
                codec,
                payload,
                streamLength,
                streamCount);
#endif
    }

    private static Measurement Measure(
        string payloadName,
        int payloadBytes,
        string operation,
        int streamLength,
        Func<int, long> run)
    {
        var streamsPerRound = Math.Max(1, DivideRoundUp(TargetItemsPerRound, streamLength));
        var warmupStreams = Math.Max(20, DivideRoundUp(WarmupItems, streamLength));
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

            wallSamples[round] =
                elapsedTicks * 1_000_000_000d / Stopwatch.Frequency / itemCount;
            cpuSamples[round] =
                (cpuAfter - cpuBefore).TotalSeconds * 1_000_000_000d / itemCount;
            allocationSamples[round] =
                (allocatedAfter - allocatedBefore) / (double)itemCount;
        }

        if (checksum == long.MinValue)
            throw new InvalidOperationException("Impossible checksum sentinel observed.");

        return new Measurement(
            payloadName,
            payloadBytes,
            operation,
            streamLength,
            streamsPerRound,
            Median(wallSamples),
            Median(cpuSamples),
            Median(allocationSamples),
            checksum);
    }

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
        T value,
        ScratchBufferWriter writer,
        int streamLength,
        int streamCount)
        where T : unmanaged
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
        {
            checksum += StreamCodecAotEvidence_PumpInterfacePerItem(
                codec,
                in value,
                writer,
                streamLength);
        }

        return checksum;
    }

    private static long RunInterfaceHoistedStreams<T>(
        IRpcCodec<T> codec,
        T value,
        ScratchBufferWriter writer,
        int streamLength,
        int streamCount)
        where T : unmanaged
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
        {
            checksum += StreamCodecAotEvidence_PumpInterfaceHoisted(
                codec,
                in value,
                writer,
                streamLength);
        }

        return checksum;
    }

    private static long RunInterfaceDeserializeStreams<T>(
        IRpcCodec<T> codec,
        ReadOnlySequence<byte> payload,
        int streamLength,
        int streamCount)
        where T : unmanaged
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
        {
            checksum += StreamCodecAotEvidence_PumpInterfaceDeserialize(
                codec,
                in payload,
                streamLength);
        }

        return checksum;
    }

    private static long RunGenericSizedStreams<T, TCodec>(
        TCodec codec,
        T value,
        ScratchBufferWriter writer,
        int streamLength,
        int streamCount)
        where T : unmanaged
        where TCodec : IRpcCodec<T>, IRpcSizedCodec<T>
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
        {
            checksum += StreamCodecAotEvidence_PumpGenericSized<T, TCodec>(
                codec,
                in value,
                writer,
                streamLength);
        }

        return checksum;
    }

    private static long RunGenericUnsizedStreams<T, TCodec>(
        TCodec codec,
        T value,
        ScratchBufferWriter writer,
        int streamLength,
        int streamCount)
        where T : unmanaged
        where TCodec : IRpcCodec<T>
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
        {
            checksum += StreamCodecAotEvidence_PumpGenericUnsized<T, TCodec>(
                codec,
                in value,
                writer,
                streamLength);
        }

        return checksum;
    }

    private static long RunGenericDeserializeStreams<T, TCodec>(
        TCodec codec,
        ReadOnlySequence<byte> payload,
        int streamLength,
        int streamCount)
        where T : unmanaged
        where TCodec : IRpcCodec<T>
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
        {
            checksum += StreamCodecAotEvidence_PumpGenericDeserialize<T, TCodec>(
                codec,
                in payload,
                streamLength);
        }

        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long StreamCodecAotEvidence_PumpInterfacePerItem<T>(
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
                sizedCodec.TryGetEncodedSize(
                    in value,
                    out var encodedBytes,
                    out var snapshot))
            {
                try
                {
                    sizedCodec.SerializeSized(
                        in value,
                        writer,
                        encodedBytes,
                        snapshot);
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
    private static long StreamCodecAotEvidence_PumpInterfaceHoisted<T>(
        IRpcCodec<T> codec,
        in T value,
        ScratchBufferWriter writer,
        int itemCount)
        where T : unmanaged
    {
        if (codec is not IRpcSizedCodec<T> sizedCodec || !sizedCodec.CanExactSize)
        {
            return StreamCodecAotEvidence_PumpInterfaceSerializeOnly(
                codec,
                in value,
                writer,
                itemCount);
        }

        long checksum = 0;
        for (var index = 0; index < itemCount; index++)
        {
            writer.Reset();
            if (sizedCodec.TryGetEncodedSize(
                    in value,
                    out var encodedBytes,
                    out var snapshot))
            {
                try
                {
                    sizedCodec.SerializeSized(
                        in value,
                        writer,
                        encodedBytes,
                        snapshot);
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
    private static long StreamCodecAotEvidence_PumpInterfaceSerializeOnly<T>(
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
    private static long StreamCodecAotEvidence_PumpInterfaceDeserialize<T>(
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
    private static long StreamCodecAotEvidence_PumpGenericSized<T, TCodec>(
        TCodec codec,
        in T value,
        ScratchBufferWriter writer,
        int itemCount)
        where T : unmanaged
        where TCodec : IRpcCodec<T>, IRpcSizedCodec<T>
    {
        if (!codec.CanExactSize)
        {
            return StreamCodecAotEvidence_PumpGenericUnsized<T, TCodec>(
                codec,
                in value,
                writer,
                itemCount);
        }

        long checksum = 0;
        for (var index = 0; index < itemCount; index++)
        {
            writer.Reset();
            if (codec.TryGetEncodedSize(
                    in value,
                    out var encodedBytes,
                    out var snapshot))
            {
                try
                {
                    codec.SerializeSized(
                        in value,
                        writer,
                        encodedBytes,
                        snapshot);
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
    private static long StreamCodecAotEvidence_PumpGenericUnsized<T, TCodec>(
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
    private static long StreamCodecAotEvidence_PumpGenericDeserialize<T, TCodec>(
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

    private static void WriteJson(
        string outputPath,
        IReadOnlyList<Measurement> measurements)
    {
        using var stream = File.Create(outputPath);
        using var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteString("commit", Environment.GetEnvironmentVariable(
            "SHARPLINK_BENCHMARK_SHA") ?? "unknown");
        writer.WriteString("runtime", "NativeAOT");
        writer.WriteString("shape", Shape);
        writer.WriteString("architecture", RuntimeInformation.ProcessArchitecture.ToString());
        writer.WriteString("os", RuntimeInformation.OSDescription);
        writer.WriteNumber("processorCount", Environment.ProcessorCount);
        writer.WriteNumber("rounds", Rounds);
        writer.WriteStartArray("measurements");

        foreach (var measurement in measurements)
        {
            writer.WriteStartObject();
            writer.WriteString("payload", measurement.Payload);
            writer.WriteNumber("payloadBytes", measurement.PayloadBytes);
            writer.WriteString("operation", measurement.Operation);
            writer.WriteNumber("streamLength", measurement.StreamLength);
            writer.WriteNumber("streamsPerRound", measurement.StreamsPerRound);
            writer.WriteNumber("nanosecondsPerItem", measurement.NanosecondsPerItem);
            writer.WriteNumber(
                "cpuNanosecondsPerItem",
                measurement.CpuNanosecondsPerItem);
            writer.WriteNumber(
                "allocatedBytesPerItem",
                measurement.AllocatedBytesPerItem);
            writer.WriteNumber("checksum", measurement.Checksum);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
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

    private sealed class UnmanagedSizedClassCodec<T> :
        IRpcCodec<T>,
        IRpcSizedCodec<T>
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

    private readonly struct UnmanagedSizedStructCodec<T> :
        IRpcCodec<T>,
        IRpcSizedCodec<T>
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

    [StructLayout(LayoutKind.Sequential)]
    private struct Payload256
    {
        public Payload64 A;
        public Payload64 B;
        public Payload64 C;
        public Payload64 D;
    }

    private sealed record Measurement(
        string Payload,
        int PayloadBytes,
        string Operation,
        int StreamLength,
        int StreamsPerRound,
        double NanosecondsPerItem,
        double CpuNanosecondsPerItem,
        double AllocatedBytesPerItem,
        long Checksum);
}
