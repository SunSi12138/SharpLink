using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using SharpLink.Abstractions;

namespace SharpLink.StreamCodecStructCoreEvidence;

internal static class StructCoreEvidenceRunner
{
    private const int Rounds = 3;
    private const int TargetItemsPerRound = 750_000;
    private const int WarmupItems = 150_000;
    private static readonly int[] MainStreamLengths = [1_000, 10_000, 100_000];
    private static readonly int[] TinyStreamLengths = [1, 8, 64];

    public static void Run(string[] args)
    {
#if STRUCT_CORE_EVIDENCE_BASELINE
        RunFixed("production-interface", args);
#elif STRUCT_CORE_EVIDENCE_SHELL
        RunFixed("shell-interface", args);
#elif STRUCT_CORE_EVIDENCE_OPEN_VALUE
        RunFixed("open-value", args);
#elif STRUCT_CORE_EVIDENCE_OPEN_IN
        RunFixed("open-in", args);
#elif STRUCT_CORE_EVIDENCE_DIRECT
        RunFixed("direct-core", args);
#else
        if (args.Length > 0 && string.Equals(args[0], "--jit-probe", StringComparison.Ordinal))
        {
            RunJitProbe(args[1..]);
            return;
        }

        if (args.Length != 2)
        {
            throw new ArgumentException(
                "Usage: <production-interface|shell-interface|open-value|open-in|direct-core> <output-json>");
        }

        RunShape(args[0], args[1]);
#endif
    }

#if STRUCT_CORE_EVIDENCE_BASELINE || STRUCT_CORE_EVIDENCE_SHELL || STRUCT_CORE_EVIDENCE_OPEN_VALUE || STRUCT_CORE_EVIDENCE_OPEN_IN || STRUCT_CORE_EVIDENCE_DIRECT
    private static void RunFixed(string shape, string[] args)
    {
        if (args.Length != 1)
            throw new ArgumentException("Usage: <output-json>");
        RunShape(shape, args[0]);
    }
#endif

    private static void RunShape(string shape, string outputPath)
    {
        if (shape is not ("production-interface" or "shell-interface" or "open-value" or "open-in" or "direct-core"))
            throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown struct-core evidence shape.");

        outputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var measurements = new List<Measurement>();
        AddFlatPayload("int", CreateValue<int>(), shape, measurements);
        AddFlatPayload("long", CreateValue<long>(), shape, measurements);
        AddFlatPayload("guid", CreateValue<Guid>(), shape, measurements);
        AddFlatPayload("payload16", CreateValue<Payload16>(), shape, measurements);
        AddFlatPayload("payload64", CreateValue<Payload64>(), shape, measurements);
        AddFlatPayload("payload256", CreateValue<Payload256>(), shape, measurements);
        AddNested64Payload(shape, measurements);
        AddTinyOpenCost(shape, measurements);

        WriteJson(outputPath, shape, measurements);
        Console.WriteLine($"Wrote struct-core evidence for {shape} to {outputPath}.");
    }

    private static void AddFlatPayload<T>(
        string payloadName,
        T value,
        string shape,
        List<Measurement> measurements)
        where T : unmanaged
    {
        foreach (var streamLength in MainStreamLengths)
            AddFlatScenario(payloadName, value, streamLength, shape, measurements);
    }

    private static void AddTinyOpenCost(
        string shape,
        List<Measurement> measurements)
    {
        var value = CreateValue<int>();
        foreach (var streamLength in TinyStreamLengths)
            AddFlatScenario("int-open-cost", value, streamLength, shape, measurements);
    }

    private static void AddFlatScenario<T>(
        string payloadName,
        T value,
        int streamLength,
        string shape,
        List<Measurement> measurements)
        where T : unmanaged
    {
#if STRUCT_CORE_EVIDENCE_BASELINE
        AddFlatProduction(payloadName, value, streamLength, measurements);
#elif STRUCT_CORE_EVIDENCE_SHELL
        AddFlatShellInterface(payloadName, value, streamLength, measurements);
#elif STRUCT_CORE_EVIDENCE_OPEN_VALUE
        AddFlatOpenValue(payloadName, value, streamLength, measurements);
#elif STRUCT_CORE_EVIDENCE_OPEN_IN
        AddFlatOpenIn(payloadName, value, streamLength, measurements);
#elif STRUCT_CORE_EVIDENCE_DIRECT
        AddFlatDirect(payloadName, value, streamLength, measurements);
#else
        switch (shape)
        {
            case "production-interface":
                AddFlatProduction(payloadName, value, streamLength, measurements);
                break;
            case "shell-interface":
                AddFlatShellInterface(payloadName, value, streamLength, measurements);
                break;
            case "open-value":
                AddFlatOpenValue(payloadName, value, streamLength, measurements);
                break;
            case "open-in":
                AddFlatOpenIn(payloadName, value, streamLength, measurements);
                break;
            case "direct-core":
                AddFlatDirect(payloadName, value, streamLength, measurements);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
#endif
    }

    private static void AddFlatProduction<T>(
        string payloadName,
        T value,
        int streamLength,
        List<Measurement> measurements)
        where T : unmanaged
    {
        IRpcCodec<T> sizedCodec = new InlineSizedClassCodec<T>();
        var exactSizeCodec = (IRpcSizedCodec<T>)sizedCodec;
        IRpcCodec<T> unsizedCodec = new InlineClassCodec<T>();
        var writer = new ScratchBufferWriter();
        var payload = CreatePayload(in value);

        AddThreeOperations(
            measurements,
            payloadName,
            "flat",
            streamLength,
            Unsafe.SizeOf<FlatSizedCore<T>>(),
            count => RunBaselineSizedStreams(sizedCodec, exactSizeCodec, in value, writer, streamLength, count),
            count => RunBaselineUnsizedStreams(unsizedCodec, in value, writer, streamLength, count),
            count => RunBaselineDeserializeStreams(sizedCodec, in payload, streamLength, count));
    }

    private static void AddFlatShellInterface<T>(
        string payloadName,
        T value,
        int streamLength,
        List<Measurement> measurements)
        where T : unmanaged
    {
        var sizedShell = new FlatSizedCodecShell<T>();
        IRpcCodec<T> sizedCodec = sizedShell;
        var exactSizeCodec = (IRpcSizedCodec<T>)sizedShell;
        IRpcCodec<T> unsizedCodec = new FlatUnsizedCodecShell<T>();
        var writer = new ScratchBufferWriter();
        var payload = CreatePayload(in value);

        AddThreeOperations(
            measurements,
            payloadName,
            "flat",
            streamLength,
            Unsafe.SizeOf<FlatSizedCore<T>>(),
            count => RunBaselineSizedStreams(sizedCodec, exactSizeCodec, in value, writer, streamLength, count),
            count => RunBaselineUnsizedStreams(unsizedCodec, in value, writer, streamLength, count),
            count => RunBaselineDeserializeStreams(sizedCodec, in payload, streamLength, count));
    }

    private static void AddFlatOpenValue<T>(
        string payloadName,
        T value,
        int streamLength,
        List<Measurement> measurements)
        where T : unmanaged
    {
        var sizedShell = new FlatSizedCodecShell<T>();
        var unsizedShell = new FlatUnsizedCodecShell<T>();
        var writer = new ScratchBufferWriter();
        var payload = CreatePayload(in value);

        AddThreeOperations(
            measurements,
            payloadName,
            "flat",
            streamLength,
            Unsafe.SizeOf<FlatSizedCore<T>>(),
            count => RunOpenedSizedStreams<T>(sizedShell, in value, writer, streamLength, count, passByIn: false),
            count => RunOpenedUnsizedStreams<T>(unsizedShell, in value, writer, streamLength, count, passByIn: false),
            count => RunOpenedDeserializeStreams<T>(sizedShell, in payload, streamLength, count, passByIn: false));
    }

    private static void AddFlatOpenIn<T>(
        string payloadName,
        T value,
        int streamLength,
        List<Measurement> measurements)
        where T : unmanaged
    {
        var sizedShell = new FlatSizedCodecShell<T>();
        var unsizedShell = new FlatUnsizedCodecShell<T>();
        var writer = new ScratchBufferWriter();
        var payload = CreatePayload(in value);

        AddThreeOperations(
            measurements,
            payloadName,
            "flat",
            streamLength,
            Unsafe.SizeOf<FlatSizedCore<T>>(),
            count => RunOpenedSizedStreams<T>(sizedShell, in value, writer, streamLength, count, passByIn: true),
            count => RunOpenedUnsizedStreams<T>(unsizedShell, in value, writer, streamLength, count, passByIn: true),
            count => RunOpenedDeserializeStreams<T>(sizedShell, in payload, streamLength, count, passByIn: true));
    }

    private static void AddFlatDirect<T>(
        string payloadName,
        T value,
        int streamLength,
        List<Measurement> measurements)
        where T : unmanaged
    {
        var sizedCore = default(FlatSizedCore<T>);
        var unsizedCore = default(FlatUnsizedCore<T>);
        var writer = new ScratchBufferWriter();
        var payload = CreatePayload(in value);

        AddThreeOperations(
            measurements,
            payloadName,
            "flat",
            streamLength,
            Unsafe.SizeOf<FlatSizedCore<T>>(),
            count => RunDirectSizedStreams<T, FlatSizedCore<T>>(sizedCore, in value, writer, streamLength, count),
            count => RunDirectUnsizedStreams<T, FlatUnsizedCore<T>>(unsizedCore, in value, writer, streamLength, count),
            count => RunDirectDeserializeStreams<T, FlatSizedCore<T>>(sizedCore, in payload, streamLength, count));
    }

    private static void AddNested64Payload(
        string shape,
        List<Measurement> measurements)
    {
        var value = CreateValue<Nested64>();
        foreach (var streamLength in MainStreamLengths)
        {
#if STRUCT_CORE_EVIDENCE_BASELINE
            AddNested64Production(value, streamLength, measurements);
#elif STRUCT_CORE_EVIDENCE_SHELL
            AddNested64ShellInterface(value, streamLength, measurements);
#elif STRUCT_CORE_EVIDENCE_OPEN_VALUE
            AddNested64Open(value, streamLength, measurements, passByIn: false);
#elif STRUCT_CORE_EVIDENCE_OPEN_IN
            AddNested64Open(value, streamLength, measurements, passByIn: true);
#elif STRUCT_CORE_EVIDENCE_DIRECT
            AddNested64Direct(value, streamLength, measurements);
#else
            switch (shape)
            {
                case "production-interface":
                    AddNested64Production(value, streamLength, measurements);
                    break;
                case "shell-interface":
                    AddNested64ShellInterface(value, streamLength, measurements);
                    break;
                case "open-value":
                    AddNested64Open(value, streamLength, measurements, passByIn: false);
                    break;
                case "open-in":
                    AddNested64Open(value, streamLength, measurements, passByIn: true);
                    break;
                case "direct-core":
                    AddNested64Direct(value, streamLength, measurements);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
            }
#endif
        }
    }

    private static void AddNested64Production(
        Nested64 value,
        int streamLength,
        List<Measurement> measurements)
    {
        var childSized = new InlineSizedClassCodec<Payload16>();
        var childUnsized = new InlineClassCodec<Payload16>();
        IRpcCodec<Nested64> sizedCodec = new Nested64InlineSizedClassCodec(
            childSized, childSized, childSized, childSized);
        var exactSizeCodec = (IRpcSizedCodec<Nested64>)sizedCodec;
        IRpcCodec<Nested64> unsizedCodec = new Nested64InlineUnsizedClassCodec(
            childUnsized, childUnsized, childUnsized, childUnsized);
        var writer = new ScratchBufferWriter();
        var payload = CreatePayload(in value);

        AddThreeOperations(
            measurements,
            "generated-like64",
            "generated-like-child-interface",
            streamLength,
            Unsafe.SizeOf<Nested64SizedCore>(),
            count => RunBaselineSizedStreams(sizedCodec, exactSizeCodec, in value, writer, streamLength, count),
            count => RunBaselineUnsizedStreams(unsizedCodec, in value, writer, streamLength, count),
            count => RunBaselineDeserializeStreams(sizedCodec, in payload, streamLength, count));
    }

    private static void AddNested64ShellInterface(
        Nested64 value,
        int streamLength,
        List<Measurement> measurements)
    {
        var childSized = new InlineSizedClassCodec<Payload16>();
        var childUnsized = new InlineClassCodec<Payload16>();
        var sizedShell = new Nested64SizedCodecShell(
            childSized, childSized, childSized, childSized);
        var unsizedShell = new Nested64UnsizedCodecShell(
            childUnsized, childUnsized, childUnsized, childUnsized);
        IRpcCodec<Nested64> sizedCodec = sizedShell;
        var exactSizeCodec = (IRpcSizedCodec<Nested64>)sizedShell;
        IRpcCodec<Nested64> unsizedCodec = unsizedShell;
        var writer = new ScratchBufferWriter();
        var payload = CreatePayload(in value);

        AddThreeOperations(
            measurements,
            "generated-like64",
            "generated-like-child-interface",
            streamLength,
            Unsafe.SizeOf<Nested64SizedCore>(),
            count => RunBaselineSizedStreams(sizedCodec, exactSizeCodec, in value, writer, streamLength, count),
            count => RunBaselineUnsizedStreams(unsizedCodec, in value, writer, streamLength, count),
            count => RunBaselineDeserializeStreams(sizedCodec, in payload, streamLength, count));
    }

    private static void AddNested64Open(
        Nested64 value,
        int streamLength,
        List<Measurement> measurements,
        bool passByIn)
    {
        var childSized = new InlineSizedClassCodec<Payload16>();
        var childUnsized = new InlineClassCodec<Payload16>();
        var sizedShell = new Nested64SizedCodecShell(
            childSized, childSized, childSized, childSized);
        var unsizedShell = new Nested64UnsizedCodecShell(
            childUnsized, childUnsized, childUnsized, childUnsized);
        var writer = new ScratchBufferWriter();
        var payload = CreatePayload(in value);

        AddThreeOperations(
            measurements,
            "generated-like64",
            "generated-like-child-interface",
            streamLength,
            Unsafe.SizeOf<Nested64SizedCore>(),
            count => RunOpenedSizedStreams<Nested64>(sizedShell, in value, writer, streamLength, count, passByIn),
            count => RunOpenedUnsizedStreams<Nested64>(unsizedShell, in value, writer, streamLength, count, passByIn),
            count => RunOpenedDeserializeStreams<Nested64>(sizedShell, in payload, streamLength, count, passByIn));
    }

    private static void AddNested64Direct(
        Nested64 value,
        int streamLength,
        List<Measurement> measurements)
    {
        var childSized = new InlineSizedClassCodec<Payload16>();
        var childUnsized = new InlineClassCodec<Payload16>();
        var sizedCore = new Nested64SizedCore(
            childSized, childSized, childSized, childSized);
        var unsizedCore = new Nested64UnsizedCore(
            childUnsized, childUnsized, childUnsized, childUnsized);
        var writer = new ScratchBufferWriter();
        var payload = CreatePayload(in value);

        AddThreeOperations(
            measurements,
            "generated-like64",
            "generated-like-child-interface",
            streamLength,
            Unsafe.SizeOf<Nested64SizedCore>(),
            count => RunDirectSizedStreams<Nested64, Nested64SizedCore>(
                sizedCore, in value, writer, streamLength, count),
            count => RunDirectUnsizedStreams<Nested64, Nested64UnsizedCore>(
                unsizedCore, in value, writer, streamLength, count),
            count => RunDirectDeserializeStreams<Nested64, Nested64SizedCore>(
                sizedCore, in payload, streamLength, count));
    }

    private static void AddThreeOperations(
        List<Measurement> measurements,
        string payload,
        string profile,
        int streamLength,
        int coreSizeBytes,
        Func<int, long> sized,
        Func<int, long> unsized,
        Func<int, long> deserialize)
    {
        measurements.Add(Measure(payload, profile, "sized-serialize", streamLength, coreSizeBytes, sized));
        measurements.Add(Measure(payload, profile, "unsized-serialize", streamLength, coreSizeBytes, unsized));
        measurements.Add(Measure(payload, profile, "deserialize", streamLength, coreSizeBytes, deserialize));
    }

    private static Measurement Measure(
        string payload,
        string profile,
        string operation,
        int streamLength,
        int coreSizeBytes,
        Func<int, long> run)
    {
        var streamsPerRound = Math.Max(1, DivideRoundUp(TargetItemsPerRound, streamLength));
        var warmupStreams = Math.Max(4, DivideRoundUp(WarmupItems, streamLength));
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
            var allocationBefore = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();

            checksum ^= run(streamsPerRound);

            var elapsed = Stopwatch.GetTimestamp() - started;
            var allocationAfter = GC.GetAllocatedBytesForCurrentThread();
            process.Refresh();
            var cpuAfter = process.TotalProcessorTime;

            wallSamples[round] = elapsed * 1_000_000_000d / Stopwatch.Frequency / itemCount;
            cpuSamples[round] = (cpuAfter - cpuBefore).TotalSeconds * 1_000_000_000d / itemCount;
            allocationSamples[round] = (allocationAfter - allocationBefore) / (double)itemCount;
        }

        if (checksum == long.MinValue)
            throw new InvalidOperationException("Impossible checksum sentinel observed.");

        return new Measurement(
            payload,
            PayloadSize(payload),
            profile,
            operation,
            streamLength,
            streamsPerRound,
            coreSizeBytes,
            Median(wallSamples),
            Median(cpuSamples),
            Median(allocationSamples),
            checksum);
    }

    private static long RunBaselineSizedStreams<T>(
        IRpcCodec<T> codec,
        IRpcSizedCodec<T> sizedCodec,
        in T value,
        ScratchBufferWriter writer,
        int streamLength,
        int streamCount)
        where T : unmanaged
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
        {
            checksum += StreamCodecStructCore_PumpBaselineSized(
                codec, sizedCodec, in value, writer, streamLength);
        }
        return checksum;
    }

    private static long RunBaselineUnsizedStreams<T>(
        IRpcCodec<T> codec,
        in T value,
        ScratchBufferWriter writer,
        int streamLength,
        int streamCount)
        where T : unmanaged
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
            checksum += StreamCodecStructCore_PumpBaselineUnsized(codec, in value, writer, streamLength);
        return checksum;
    }

    private static long RunBaselineDeserializeStreams<T>(
        IRpcCodec<T> codec,
        in ReadOnlySequence<byte> payload,
        int streamLength,
        int streamCount)
        where T : unmanaged
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
            checksum += StreamCodecStructCore_PumpBaselineDeserialize(codec, in payload, streamLength);
        return checksum;
    }

    private static long RunOpenedSizedStreams<T>(
        ISizedCoreOpener<T> opener,
        in T value,
        ScratchBufferWriter writer,
        int streamLength,
        int streamCount,
        bool passByIn)
        where T : unmanaged
    {
        var state = new SizedState<T>(value, writer, streamLength);
        long checksum = 0;
        if (passByIn)
        {
            var visitor = new SizedInVisitor<T>(state);
            for (var stream = 0; stream < streamCount; stream++)
                checksum += opener.OpenSizedIn(ref visitor);
        }
        else
        {
            var visitor = new SizedVisitor<T>(state);
            for (var stream = 0; stream < streamCount; stream++)
                checksum += opener.OpenSized(ref visitor);
        }
        return checksum;
    }

    private static long RunOpenedUnsizedStreams<T>(
        ICoreOpener<T> opener,
        in T value,
        ScratchBufferWriter writer,
        int streamLength,
        int streamCount,
        bool passByIn)
        where T : unmanaged
    {
        var state = new SerializeState<T>(value, writer, streamLength);
        long checksum = 0;
        if (passByIn)
        {
            var visitor = new SerializeInVisitor<T>(state);
            for (var stream = 0; stream < streamCount; stream++)
                checksum += opener.OpenIn(ref visitor);
        }
        else
        {
            var visitor = new SerializeVisitor<T>(state);
            for (var stream = 0; stream < streamCount; stream++)
                checksum += opener.Open(ref visitor);
        }
        return checksum;
    }

    private static long RunOpenedDeserializeStreams<T>(
        ICoreOpener<T> opener,
        in ReadOnlySequence<byte> payload,
        int streamLength,
        int streamCount,
        bool passByIn)
        where T : unmanaged
    {
        var state = new DeserializeState<T>(payload, streamLength);
        long checksum = 0;
        if (passByIn)
        {
            var visitor = new DeserializeInVisitor<T>(state);
            for (var stream = 0; stream < streamCount; stream++)
                checksum += opener.OpenIn(ref visitor);
        }
        else
        {
            var visitor = new DeserializeVisitor<T>(state);
            for (var stream = 0; stream < streamCount; stream++)
                checksum += opener.Open(ref visitor);
        }
        return checksum;
    }

    private static long RunDirectSizedStreams<T, TCore>(
        TCore core,
        in T value,
        ScratchBufferWriter writer,
        int streamLength,
        int streamCount)
        where T : unmanaged
        where TCore : struct, IRpcCodec<T>, IRpcSizedCodec<T>
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
            checksum += StreamCodecStructCore_PumpGenericSized(core, in value, writer, streamLength);
        return checksum;
    }

    private static long RunDirectUnsizedStreams<T, TCore>(
        TCore core,
        in T value,
        ScratchBufferWriter writer,
        int streamLength,
        int streamCount)
        where T : unmanaged
        where TCore : struct, IRpcCodec<T>
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
            checksum += StreamCodecStructCore_PumpGenericUnsized(core, in value, writer, streamLength);
        return checksum;
    }

    private static long RunDirectDeserializeStreams<T, TCore>(
        TCore core,
        in ReadOnlySequence<byte> payload,
        int streamLength,
        int streamCount)
        where T : unmanaged
        where TCore : struct, IRpcCodec<T>
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
            checksum += StreamCodecStructCore_PumpGenericDeserialize<T, TCore>(core, in payload, streamLength);
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long StreamCodecStructCore_PumpBaselineSized<T>(
        IRpcCodec<T> codec,
        IRpcSizedCodec<T> sizedCodec,
        in T value,
        ScratchBufferWriter writer,
        int itemCount)
        where T : unmanaged
    {
        long checksum = 0;
        for (var index = 0; index < itemCount; index++)
        {
            writer.Reset();
            if (sizedCodec.TryGetEncodedSize(in value, out var size, out var snapshot))
            {
                try
                {
                    sizedCodec.SerializeSized(in value, writer, size, snapshot);
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
    private static long StreamCodecStructCore_PumpBaselineUnsized<T>(
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
    private static long StreamCodecStructCore_PumpBaselineDeserialize<T>(
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
    private static long StreamCodecStructCore_PumpGenericSized<T, TCore>(
        TCore core,
        in T value,
        ScratchBufferWriter writer,
        int itemCount)
        where T : unmanaged
        where TCore : struct, IRpcCodec<T>, IRpcSizedCodec<T>
    {
        long checksum = 0;
        for (var index = 0; index < itemCount; index++)
        {
            writer.Reset();
            if (core.TryGetEncodedSize(in value, out var size, out var snapshot))
            {
                try
                {
                    core.SerializeSized(in value, writer, size, snapshot);
                }
                finally
                {
                    if (snapshot is not null)
                        core.ReleaseSnapshot(snapshot);
                }
            }
            else
            {
                core.Serialize(in value, writer);
            }
            checksum += writer.WrittenSpan[0];
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long StreamCodecStructCore_PumpGenericSizedIn<T, TCore>(
        in TCore core,
        in T value,
        ScratchBufferWriter writer,
        int itemCount)
        where T : unmanaged
        where TCore : struct, IRpcCodec<T>, IRpcSizedCodec<T>
    {
        long checksum = 0;
        for (var index = 0; index < itemCount; index++)
        {
            writer.Reset();
            if (core.TryGetEncodedSize(in value, out var size, out var snapshot))
            {
                try
                {
                    core.SerializeSized(in value, writer, size, snapshot);
                }
                finally
                {
                    if (snapshot is not null)
                        core.ReleaseSnapshot(snapshot);
                }
            }
            else
            {
                core.Serialize(in value, writer);
            }
            checksum += writer.WrittenSpan[0];
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long StreamCodecStructCore_PumpGenericUnsized<T, TCore>(
        TCore core,
        in T value,
        ScratchBufferWriter writer,
        int itemCount)
        where T : unmanaged
        where TCore : struct, IRpcCodec<T>
    {
        long checksum = 0;
        for (var index = 0; index < itemCount; index++)
        {
            writer.Reset();
            core.Serialize(in value, writer);
            checksum += writer.WrittenSpan[0];
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long StreamCodecStructCore_PumpGenericUnsizedIn<T, TCore>(
        in TCore core,
        in T value,
        ScratchBufferWriter writer,
        int itemCount)
        where T : unmanaged
        where TCore : struct, IRpcCodec<T>
    {
        long checksum = 0;
        for (var index = 0; index < itemCount; index++)
        {
            writer.Reset();
            core.Serialize(in value, writer);
            checksum += writer.WrittenSpan[0];
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long StreamCodecStructCore_PumpGenericDeserialize<T, TCore>(
        TCore core,
        in ReadOnlySequence<byte> payload,
        int itemCount)
        where T : unmanaged
        where TCore : struct, IRpcCodec<T>
    {
        long checksum = 0;
        for (var index = 0; index < itemCount; index++)
        {
            var item = core.Deserialize(in payload);
            checksum += Unsafe.As<T, byte>(ref item);
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long StreamCodecStructCore_PumpGenericDeserializeIn<T, TCore>(
        in TCore core,
        in ReadOnlySequence<byte> payload,
        int itemCount)
        where T : unmanaged
        where TCore : struct, IRpcCodec<T>
    {
        long checksum = 0;
        for (var index = 0; index < itemCount; index++)
        {
            var item = core.Deserialize(in payload);
            checksum += Unsafe.As<T, byte>(ref item);
        }
        return checksum;
    }

#if !STRUCT_CORE_EVIDENCE_BASELINE && !STRUCT_CORE_EVIDENCE_SHELL && !STRUCT_CORE_EVIDENCE_OPEN_VALUE && !STRUCT_CORE_EVIDENCE_OPEN_IN && !STRUCT_CORE_EVIDENCE_DIRECT
    private static void RunJitProbe(string[] args)
    {
        if (args.Length != 2)
            throw new ArgumentException("Usage: --jit-probe <shape> <stream-count>");

        var shape = args[0];
        var streamCount = int.Parse(args[1], CultureInfo.InvariantCulture);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(streamCount);

        var value = CreateValue<int>();
        var writer = new ScratchBufferWriter();
        var payload = CreatePayload(in value);
        long checksum;

        switch (shape)
        {
            case "production-interface":
                {
                    IRpcCodec<int> codec = new InlineSizedClassCodec<int>();
                    checksum = RunBaselineSizedStreams(
                        codec, (IRpcSizedCodec<int>)codec, in value, writer, 10_000, streamCount);
                    checksum += RunBaselineDeserializeStreams(codec, in payload, 10_000, streamCount);
                    break;
                }
            case "shell-interface":
                {
                    var shell = new FlatSizedCodecShell<int>();
                    IRpcCodec<int> codec = shell;
                    checksum = RunBaselineSizedStreams(
                        codec, shell, in value, writer, 10_000, streamCount);
                    checksum += RunBaselineDeserializeStreams(codec, in payload, 10_000, streamCount);
                    break;
                }
            case "open-value":
                {
                    var shell = new FlatSizedCodecShell<int>();
                    checksum = RunOpenedSizedStreams<int>(
                        shell, in value, writer, 10_000, streamCount, passByIn: false);
                    checksum += RunOpenedDeserializeStreams<int>(
                        shell, in payload, 10_000, streamCount, passByIn: false);
                    break;
                }
            case "open-in":
                {
                    var shell = new FlatSizedCodecShell<int>();
                    checksum = RunOpenedSizedStreams<int>(
                        shell, in value, writer, 10_000, streamCount, passByIn: true);
                    checksum += RunOpenedDeserializeStreams<int>(
                        shell, in payload, 10_000, streamCount, passByIn: true);
                    break;
                }
            case "direct-core":
                {
                    var core = default(FlatSizedCore<int>);
                    checksum = RunDirectSizedStreams<int, FlatSizedCore<int>>(
                        core, in value, writer, 10_000, streamCount);
                    checksum += RunDirectDeserializeStreams<int, FlatSizedCore<int>>(
                        core, in payload, 10_000, streamCount);
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }

        Console.WriteLine("struct-core-jit-probe checksum=" + checksum);
    }
#endif

    private interface ICoreVisitor<T>
        where T : unmanaged
    {
        long Visit<TCore>(TCore core)
            where TCore : struct, IRpcCodec<T>;
    }

    private interface ICoreInVisitor<T>
        where T : unmanaged
    {
        long Visit<TCore>(in TCore core)
            where TCore : struct, IRpcCodec<T>;
    }

    private interface ISizedCoreVisitor<T>
        where T : unmanaged
    {
        long Visit<TCore>(TCore core)
            where TCore : struct, IRpcCodec<T>, IRpcSizedCodec<T>;
    }

    private interface ISizedCoreInVisitor<T>
        where T : unmanaged
    {
        long Visit<TCore>(in TCore core)
            where TCore : struct, IRpcCodec<T>, IRpcSizedCodec<T>;
    }

    private interface ICoreOpener<T>
        where T : unmanaged
    {
        long Open<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ICoreVisitor<T>;

        long OpenIn<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ICoreInVisitor<T>;
    }

    private interface ISizedCoreOpener<T> : ICoreOpener<T>
        where T : unmanaged
    {
        long OpenSized<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ISizedCoreVisitor<T>;

        long OpenSizedIn<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ISizedCoreInVisitor<T>;
    }

    private class SerializeState<T>
        where T : unmanaged
    {
        public SerializeState(T value, ScratchBufferWriter writer, int itemCount)
        {
            Value = value;
            Writer = writer;
            ItemCount = itemCount;
        }

        public T Value;
        public ScratchBufferWriter Writer;
        public int ItemCount;
    }

    private sealed class SizedState<T> : SerializeState<T>
        where T : unmanaged
    {
        public SizedState(T value, ScratchBufferWriter writer, int itemCount)
            : base(value, writer, itemCount)
        {
        }
    }

    private sealed class DeserializeState<T>
        where T : unmanaged
    {
        public DeserializeState(ReadOnlySequence<byte> payload, int itemCount)
        {
            Payload = payload;
            ItemCount = itemCount;
        }

        public ReadOnlySequence<byte> Payload;
        public int ItemCount;
    }

    private readonly struct SerializeVisitor<T> : ICoreVisitor<T>
        where T : unmanaged
    {
        private readonly SerializeState<T> _state;

        public SerializeVisitor(SerializeState<T> state) => _state = state;

        public long Visit<TCore>(TCore core)
            where TCore : struct, IRpcCodec<T>
            => StreamCodecStructCore_PumpGenericUnsized(
                core, in _state.Value, _state.Writer, _state.ItemCount);
    }

    private readonly struct SerializeInVisitor<T> : ICoreInVisitor<T>
        where T : unmanaged
    {
        private readonly SerializeState<T> _state;

        public SerializeInVisitor(SerializeState<T> state) => _state = state;

        public long Visit<TCore>(in TCore core)
            where TCore : struct, IRpcCodec<T>
            => StreamCodecStructCore_PumpGenericUnsizedIn(
                in core, in _state.Value, _state.Writer, _state.ItemCount);
    }

    private readonly struct SizedVisitor<T> : ISizedCoreVisitor<T>
        where T : unmanaged
    {
        private readonly SizedState<T> _state;

        public SizedVisitor(SizedState<T> state) => _state = state;

        public long Visit<TCore>(TCore core)
            where TCore : struct, IRpcCodec<T>, IRpcSizedCodec<T>
            => StreamCodecStructCore_PumpGenericSized(
                core, in _state.Value, _state.Writer, _state.ItemCount);
    }

    private readonly struct SizedInVisitor<T> : ISizedCoreInVisitor<T>
        where T : unmanaged
    {
        private readonly SizedState<T> _state;

        public SizedInVisitor(SizedState<T> state) => _state = state;

        public long Visit<TCore>(in TCore core)
            where TCore : struct, IRpcCodec<T>, IRpcSizedCodec<T>
            => StreamCodecStructCore_PumpGenericSizedIn(
                in core, in _state.Value, _state.Writer, _state.ItemCount);
    }

    private readonly struct DeserializeVisitor<T> : ICoreVisitor<T>
        where T : unmanaged
    {
        private readonly DeserializeState<T> _state;

        public DeserializeVisitor(DeserializeState<T> state) => _state = state;

        public long Visit<TCore>(TCore core)
            where TCore : struct, IRpcCodec<T>
            => StreamCodecStructCore_PumpGenericDeserialize<T, TCore>(
                core, in _state.Payload, _state.ItemCount);
    }

    private readonly struct DeserializeInVisitor<T> : ICoreInVisitor<T>
        where T : unmanaged
    {
        private readonly DeserializeState<T> _state;

        public DeserializeInVisitor(DeserializeState<T> state) => _state = state;

        public long Visit<TCore>(in TCore core)
            where TCore : struct, IRpcCodec<T>
            => StreamCodecStructCore_PumpGenericDeserializeIn<T, TCore>(
                in core, in _state.Payload, _state.ItemCount);
    }

    private static class UnmanagedOperations<T>
        where T : unmanaged
    {
        public static int Size => Unsafe.SizeOf<T>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize(in T value, IBufferWriter<byte> writer)
        {
            var span = writer.GetSpan(Size);
            MemoryMarshal.Write(span, in value);
            writer.Advance(Size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Deserialize(in ReadOnlySequence<byte> payload)
            => MemoryMarshal.Read<T>(payload.FirstSpan);
    }

    private sealed class InlineClassCodec<T> : IRpcCodec<T>
        where T : unmanaged
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(in T value, IBufferWriter<byte> buffer)
            => UnmanagedOperations<T>.Serialize(in value, buffer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Deserialize(in ReadOnlySequence<byte> buffer)
            => UnmanagedOperations<T>.Deserialize(in buffer);
    }

    private sealed class InlineSizedClassCodec<T> : IRpcCodec<T>, IRpcSizedCodec<T>
        where T : unmanaged
    {
        public bool CanExactSize => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(in T value, IBufferWriter<byte> buffer)
            => UnmanagedOperations<T>.Serialize(in value, buffer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Deserialize(in ReadOnlySequence<byte> buffer)
            => UnmanagedOperations<T>.Deserialize(in buffer);

        public bool TryGetEncodedSize(in T value, out int size)
        {
            size = UnmanagedOperations<T>.Size;
            return true;
        }

        public bool TryGetEncodedSize(
            in T value,
            out int size,
            out IRpcSizedCodecSnapshot? snapshot)
        {
            size = UnmanagedOperations<T>.Size;
            snapshot = null;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SerializeSized(
            in T value,
            IBufferWriter<byte> buffer,
            int size,
            IRpcSizedCodecSnapshot? snapshot)
            => UnmanagedOperations<T>.Serialize(in value, buffer);

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
        {
        }
    }

    private readonly struct FlatUnsizedCore<T> : IRpcCodec<T>
        where T : unmanaged
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(in T value, IBufferWriter<byte> buffer)
            => UnmanagedOperations<T>.Serialize(in value, buffer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Deserialize(in ReadOnlySequence<byte> buffer)
            => UnmanagedOperations<T>.Deserialize(in buffer);
    }

    private readonly struct FlatSizedCore<T> : IRpcCodec<T>, IRpcSizedCodec<T>
        where T : unmanaged
    {
        public bool CanExactSize => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(in T value, IBufferWriter<byte> buffer)
            => UnmanagedOperations<T>.Serialize(in value, buffer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Deserialize(in ReadOnlySequence<byte> buffer)
            => UnmanagedOperations<T>.Deserialize(in buffer);

        public bool TryGetEncodedSize(in T value, out int size)
        {
            size = UnmanagedOperations<T>.Size;
            return true;
        }

        public bool TryGetEncodedSize(
            in T value,
            out int size,
            out IRpcSizedCodecSnapshot? snapshot)
        {
            size = UnmanagedOperations<T>.Size;
            snapshot = null;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SerializeSized(
            in T value,
            IBufferWriter<byte> buffer,
            int size,
            IRpcSizedCodecSnapshot? snapshot)
            => UnmanagedOperations<T>.Serialize(in value, buffer);

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
        {
        }
    }

    private sealed class FlatUnsizedCodecShell<T> : IRpcCodec<T>, ICoreOpener<T>
        where T : unmanaged
    {
        private readonly FlatUnsizedCore<T> _core = default;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(in T value, IBufferWriter<byte> buffer)
            => _core.Serialize(in value, buffer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Deserialize(in ReadOnlySequence<byte> buffer)
            => _core.Deserialize(in buffer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long Open<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ICoreVisitor<T>
            => visitor.Visit(_core);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long OpenIn<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ICoreInVisitor<T>
            => visitor.Visit(in _core);
    }

    private sealed class FlatSizedCodecShell<T> :
        IRpcCodec<T>,
        IRpcSizedCodec<T>,
        ISizedCoreOpener<T>
        where T : unmanaged
    {
        private readonly FlatSizedCore<T> _core = default;

        public bool CanExactSize => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(in T value, IBufferWriter<byte> buffer)
            => _core.Serialize(in value, buffer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Deserialize(in ReadOnlySequence<byte> buffer)
            => _core.Deserialize(in buffer);

        public bool TryGetEncodedSize(in T value, out int size)
            => _core.TryGetEncodedSize(in value, out size);

        public bool TryGetEncodedSize(
            in T value,
            out int size,
            out IRpcSizedCodecSnapshot? snapshot)
            => _core.TryGetEncodedSize(in value, out size, out snapshot);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SerializeSized(
            in T value,
            IBufferWriter<byte> buffer,
            int size,
            IRpcSizedCodecSnapshot? snapshot)
            => _core.SerializeSized(in value, buffer, size, snapshot);

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
            => _core.ReleaseSnapshot(snapshot);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long Open<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ICoreVisitor<T>
            => visitor.Visit(_core);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long OpenIn<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ICoreInVisitor<T>
            => visitor.Visit(in _core);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long OpenSized<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ISizedCoreVisitor<T>
            => visitor.Visit(_core);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long OpenSizedIn<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ISizedCoreInVisitor<T>
            => visitor.Visit(in _core);
    }

    private sealed class Nested64InlineUnsizedClassCodec : IRpcCodec<Nested64>
    {
        private readonly IRpcCodec<Payload16> _a;
        private readonly IRpcCodec<Payload16> _b;
        private readonly IRpcCodec<Payload16> _c;
        private readonly IRpcCodec<Payload16> _d;

        public Nested64InlineUnsizedClassCodec(
            IRpcCodec<Payload16> a,
            IRpcCodec<Payload16> b,
            IRpcCodec<Payload16> c,
            IRpcCodec<Payload16> d)
        {
            _a = a;
            _b = b;
            _c = c;
            _d = d;
        }

        public void Serialize(in Nested64 value, IBufferWriter<byte> buffer)
        {
            _a.Serialize(in value.A, buffer);
            _b.Serialize(in value.B, buffer);
            _c.Serialize(in value.C, buffer);
            _d.Serialize(in value.D, buffer);
        }

        public Nested64 Deserialize(in ReadOnlySequence<byte> buffer)
            => new()
            {
                A = _a.Deserialize(buffer.Slice(0, 16)),
                B = _b.Deserialize(buffer.Slice(16, 16)),
                C = _c.Deserialize(buffer.Slice(32, 16)),
                D = _d.Deserialize(buffer.Slice(48, 16))
            };
    }

    private sealed class Nested64InlineSizedClassCodec :
        IRpcCodec<Nested64>,
        IRpcSizedCodec<Nested64>
    {
        private readonly IRpcCodec<Payload16> _a;
        private readonly IRpcCodec<Payload16> _b;
        private readonly IRpcCodec<Payload16> _c;
        private readonly IRpcCodec<Payload16> _d;

        public Nested64InlineSizedClassCodec(
            IRpcCodec<Payload16> a,
            IRpcCodec<Payload16> b,
            IRpcCodec<Payload16> c,
            IRpcCodec<Payload16> d)
        {
            _a = a;
            _b = b;
            _c = c;
            _d = d;
        }

        public bool CanExactSize => true;

        public void Serialize(in Nested64 value, IBufferWriter<byte> buffer)
        {
            _a.Serialize(in value.A, buffer);
            _b.Serialize(in value.B, buffer);
            _c.Serialize(in value.C, buffer);
            _d.Serialize(in value.D, buffer);
        }

        public Nested64 Deserialize(in ReadOnlySequence<byte> buffer)
            => new()
            {
                A = _a.Deserialize(buffer.Slice(0, 16)),
                B = _b.Deserialize(buffer.Slice(16, 16)),
                C = _c.Deserialize(buffer.Slice(32, 16)),
                D = _d.Deserialize(buffer.Slice(48, 16))
            };

        public bool TryGetEncodedSize(in Nested64 value, out int size)
        {
            size = 64;
            return true;
        }

        public bool TryGetEncodedSize(
            in Nested64 value,
            out int size,
            out IRpcSizedCodecSnapshot? snapshot)
        {
            size = 64;
            snapshot = null;
            return true;
        }

        public void SerializeSized(
            in Nested64 value,
            IBufferWriter<byte> buffer,
            int size,
            IRpcSizedCodecSnapshot? snapshot)
        {
            ((IRpcSizedCodec<Payload16>)_a).SerializeSized(in value.A, buffer, 16, null);
            ((IRpcSizedCodec<Payload16>)_b).SerializeSized(in value.B, buffer, 16, null);
            ((IRpcSizedCodec<Payload16>)_c).SerializeSized(in value.C, buffer, 16, null);
            ((IRpcSizedCodec<Payload16>)_d).SerializeSized(in value.D, buffer, 16, null);
        }

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
        {
        }
    }

    private readonly struct Nested64UnsizedCore : IRpcCodec<Nested64>
    {
        private readonly IRpcCodec<Payload16> _a;
        private readonly IRpcCodec<Payload16> _b;
        private readonly IRpcCodec<Payload16> _c;
        private readonly IRpcCodec<Payload16> _d;

        public Nested64UnsizedCore(
            IRpcCodec<Payload16> a,
            IRpcCodec<Payload16> b,
            IRpcCodec<Payload16> c,
            IRpcCodec<Payload16> d)
        {
            _a = a;
            _b = b;
            _c = c;
            _d = d;
        }

        public void Serialize(in Nested64 value, IBufferWriter<byte> buffer)
        {
            _a.Serialize(in value.A, buffer);
            _b.Serialize(in value.B, buffer);
            _c.Serialize(in value.C, buffer);
            _d.Serialize(in value.D, buffer);
        }

        public Nested64 Deserialize(in ReadOnlySequence<byte> buffer)
            => new()
            {
                A = _a.Deserialize(buffer.Slice(0, 16)),
                B = _b.Deserialize(buffer.Slice(16, 16)),
                C = _c.Deserialize(buffer.Slice(32, 16)),
                D = _d.Deserialize(buffer.Slice(48, 16))
            };
    }

    private readonly struct Nested64SizedCore :
        IRpcCodec<Nested64>,
        IRpcSizedCodec<Nested64>
    {
        private readonly IRpcCodec<Payload16> _a;
        private readonly IRpcCodec<Payload16> _b;
        private readonly IRpcCodec<Payload16> _c;
        private readonly IRpcCodec<Payload16> _d;

        public Nested64SizedCore(
            IRpcCodec<Payload16> a,
            IRpcCodec<Payload16> b,
            IRpcCodec<Payload16> c,
            IRpcCodec<Payload16> d)
        {
            _a = a;
            _b = b;
            _c = c;
            _d = d;
        }

        public bool CanExactSize => true;

        public void Serialize(in Nested64 value, IBufferWriter<byte> buffer)
        {
            _a.Serialize(in value.A, buffer);
            _b.Serialize(in value.B, buffer);
            _c.Serialize(in value.C, buffer);
            _d.Serialize(in value.D, buffer);
        }

        public Nested64 Deserialize(in ReadOnlySequence<byte> buffer)
            => new()
            {
                A = _a.Deserialize(buffer.Slice(0, 16)),
                B = _b.Deserialize(buffer.Slice(16, 16)),
                C = _c.Deserialize(buffer.Slice(32, 16)),
                D = _d.Deserialize(buffer.Slice(48, 16))
            };

        public bool TryGetEncodedSize(in Nested64 value, out int size)
        {
            size = 64;
            return true;
        }

        public bool TryGetEncodedSize(
            in Nested64 value,
            out int size,
            out IRpcSizedCodecSnapshot? snapshot)
        {
            size = 64;
            snapshot = null;
            return true;
        }

        public void SerializeSized(
            in Nested64 value,
            IBufferWriter<byte> buffer,
            int size,
            IRpcSizedCodecSnapshot? snapshot)
        {
            ((IRpcSizedCodec<Payload16>)_a).SerializeSized(in value.A, buffer, 16, null);
            ((IRpcSizedCodec<Payload16>)_b).SerializeSized(in value.B, buffer, 16, null);
            ((IRpcSizedCodec<Payload16>)_c).SerializeSized(in value.C, buffer, 16, null);
            ((IRpcSizedCodec<Payload16>)_d).SerializeSized(in value.D, buffer, 16, null);
        }

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
        {
        }
    }

    private sealed class Nested64UnsizedCodecShell : IRpcCodec<Nested64>, ICoreOpener<Nested64>
    {
        private readonly Nested64UnsizedCore _core;

        public Nested64UnsizedCodecShell(
            IRpcCodec<Payload16> a,
            IRpcCodec<Payload16> b,
            IRpcCodec<Payload16> c,
            IRpcCodec<Payload16> d)
            => _core = new Nested64UnsizedCore(a, b, c, d);

        public void Serialize(in Nested64 value, IBufferWriter<byte> buffer)
            => _core.Serialize(in value, buffer);

        public Nested64 Deserialize(in ReadOnlySequence<byte> buffer)
            => _core.Deserialize(in buffer);

        public long Open<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ICoreVisitor<Nested64>
            => visitor.Visit(_core);

        public long OpenIn<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ICoreInVisitor<Nested64>
            => visitor.Visit(in _core);
    }

    private sealed class Nested64SizedCodecShell :
        IRpcCodec<Nested64>,
        IRpcSizedCodec<Nested64>,
        ISizedCoreOpener<Nested64>
    {
        private readonly Nested64SizedCore _core;

        public Nested64SizedCodecShell(
            IRpcCodec<Payload16> a,
            IRpcCodec<Payload16> b,
            IRpcCodec<Payload16> c,
            IRpcCodec<Payload16> d)
            => _core = new Nested64SizedCore(a, b, c, d);

        public bool CanExactSize => true;

        public void Serialize(in Nested64 value, IBufferWriter<byte> buffer)
            => _core.Serialize(in value, buffer);

        public Nested64 Deserialize(in ReadOnlySequence<byte> buffer)
            => _core.Deserialize(in buffer);

        public bool TryGetEncodedSize(in Nested64 value, out int size)
            => _core.TryGetEncodedSize(in value, out size);

        public bool TryGetEncodedSize(
            in Nested64 value,
            out int size,
            out IRpcSizedCodecSnapshot? snapshot)
            => _core.TryGetEncodedSize(in value, out size, out snapshot);

        public void SerializeSized(
            in Nested64 value,
            IBufferWriter<byte> buffer,
            int size,
            IRpcSizedCodecSnapshot? snapshot)
            => _core.SerializeSized(in value, buffer, size, snapshot);

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
            => _core.ReleaseSnapshot(snapshot);

        public long Open<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ICoreVisitor<Nested64>
            => visitor.Visit(_core);

        public long OpenIn<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ICoreInVisitor<Nested64>
            => visitor.Visit(in _core);

        public long OpenSized<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ISizedCoreVisitor<Nested64>
            => visitor.Visit(_core);

        public long OpenSizedIn<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ISizedCoreInVisitor<Nested64>
            => visitor.Visit(in _core);
    }

    private sealed class ScratchBufferWriter : IBufferWriter<byte>
    {
        private readonly byte[] _buffer = new byte[2048];
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

    private static int PayloadSize(string payload)
        => payload switch
        {
            "int" or "int-open-cost" => 4,
            "long" => 8,
            "guid" or "payload16" => 16,
            "payload64" or "generated-like64" => 64,
            "payload256" => 256,
            _ => 0
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

    private static void WriteJson(
        string outputPath,
        string shape,
        IReadOnlyList<Measurement> measurements)
    {
        using var stream = File.Create(outputPath);
        using var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteString(
            "commit",
            Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA") ?? "unknown");
        writer.WriteString("shape", shape);
        writer.WriteString("framework", RuntimeInformation.FrameworkDescription);
        writer.WriteString("os", RuntimeInformation.OSDescription);
        writer.WriteString("architecture", RuntimeInformation.ProcessArchitecture.ToString());
        writer.WriteNumber("processorCount", Environment.ProcessorCount);
        writer.WriteString(
            "tieredCompilation",
            Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default");
        writer.WriteString(
            "tieredPgo",
            Environment.GetEnvironmentVariable("DOTNET_TieredPGO") ?? "default");
        writer.WriteNumber("rounds", Rounds);
        writer.WriteNumber("targetItemsPerRound", TargetItemsPerRound);
        writer.WriteStartArray("notes");
        writer.WriteStringValue("production-interface models the post-#724 baseline: capability is already hoisted to stream scope while item calls stay interface-based.");
        writer.WriteStringValue("shell-interface keeps the same per-item interface calls but makes the class implementation forward into a readonly struct core.");
        writer.WriteStringValue("open-value performs one erased class/opener dispatch per stream, then runs the full item loop as Pump<T,TCore> with a concrete struct core passed by value.");
        writer.WriteStringValue("open-in is the same architecture but opens and passes TCore by readonly reference to measure struct-copy tradeoffs.");
        writer.WriteStringValue("direct-core models a statically-known generated caller entering Pump<T,TCore> without the one-per-stream opener dispatch.");
        writer.WriteStringValue("generated-like64 uses a multi-reference struct core that retains child IRpcCodec<Payload16> interface fields, modeling residual child-codec dispatch.");
        writer.WriteStringValue("1/8/64 item int-open-cost rows quantify opener amortization; the issue matrix rows remain 1k/10k/100k.");
        writer.WriteEndArray();
        writer.WriteStartArray("measurements");

        foreach (var measurement in measurements)
        {
            writer.WriteStartObject();
            writer.WriteString("payload", measurement.Payload);
            writer.WriteNumber("payloadBytes", measurement.PayloadBytes);
            writer.WriteString("profile", measurement.Profile);
            writer.WriteString("operation", measurement.Operation);
            writer.WriteNumber("streamLength", measurement.StreamLength);
            writer.WriteNumber("streamsPerRound", measurement.StreamsPerRound);
            writer.WriteNumber("coreSizeBytes", measurement.CoreSizeBytes);
            writer.WriteNumber("nanosecondsPerItem", measurement.NanosecondsPerItem);
            writer.WriteNumber("cpuNanosecondsPerItem", measurement.CpuNanosecondsPerItem);
            writer.WriteNumber("allocatedBytesPerItem", measurement.AllocatedBytesPerItem);
            writer.WriteNumber("checksum", measurement.Checksum);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
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

    [StructLayout(LayoutKind.Sequential)]
    private struct Nested64
    {
        public Payload16 A;
        public Payload16 B;
        public Payload16 C;
        public Payload16 D;
    }

    private sealed record Measurement(
        string Payload,
        int PayloadBytes,
        string Profile,
        string Operation,
        int StreamLength,
        int StreamsPerRound,
        int CoreSizeBytes,
        double NanosecondsPerItem,
        double CpuNanosecondsPerItem,
        double AllocatedBytesPerItem,
        long Checksum);
}
