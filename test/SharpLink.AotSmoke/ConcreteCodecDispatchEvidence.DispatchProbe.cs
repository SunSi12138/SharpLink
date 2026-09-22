using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using SharpLink.Abstractions;

namespace SharpLink.AotSmoke;

internal static partial class ConcreteCodecDispatchEvidence
{
    private static EvidenceCase MeasureTightLoopCase(
        string name,
        int warmupOperations,
        int operations,
        int sampleCount,
        Func<int, long> loop)
    {
        Interlocked.Add(ref s_sink, loop(warmupOperations));

        var samples = new List<EvidenceSample>(sampleCount);
        for (var sample = 0; sample < sampleCount; sample++)
        {
            ForceGc();
            samples.Add(MeasureTightLoopSample(operations, loop));
        }
        return BuildCase(name, operations, samples);
    }

    private static EvidenceSample MeasureTightLoopSample(
        int operations,
        Func<int, long> loop)
    {
        using var process = Process.GetCurrentProcess();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var cpuBefore = process.TotalProcessorTime;
        var started = Stopwatch.GetTimestamp();
        var checksum = loop(operations);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var cpu = process.TotalProcessorTime - cpuBefore;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        Interlocked.Add(ref s_sink, checksum);
        return new EvidenceSample(
            elapsed.TotalNanoseconds / operations,
            cpu.TotalNanoseconds / operations,
            (double)allocated / operations);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RunDispatchProbeSerializeInterface(
        IRpcCodec<int> codec,
        DispatchProbeCodec concrete,
        IBufferWriter<byte> writer,
        int operations)
    {
        concrete.Reset();
        var value = 42;
        for (var index = 0; index < operations; index++)
            codec.Serialize(in value, writer);
        return concrete.State;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RunDispatchProbeSerializeConcrete(
        DispatchProbeCodec codec,
        IBufferWriter<byte> writer,
        int operations)
    {
        codec.Reset();
        var value = 42;
        for (var index = 0; index < operations; index++)
            codec.Serialize(in value, writer);
        return codec.State;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RunDispatchProbeDeserializeInterface(
        IRpcCodec<int> codec,
        DispatchProbeCodec concrete,
        in ReadOnlySequence<byte> payload,
        int operations)
    {
        concrete.Reset();
        long checksum = 0;
        for (var index = 0; index < operations; index++)
            checksum += codec.Deserialize(in payload);
        return checksum ^ concrete.State;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RunDispatchProbeDeserializeConcrete(
        DispatchProbeCodec codec,
        in ReadOnlySequence<byte> payload,
        int operations)
    {
        codec.Reset();
        long checksum = 0;
        for (var index = 0; index < operations; index++)
            checksum += codec.Deserialize(in payload);
        return checksum ^ codec.State;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RunDispatchProbeDeserializeUnusedInterface(
        IRpcCodec<int> codec,
        DispatchProbeCodec concrete,
        in ReadOnlySequence<byte> payload,
        int operations)
    {
        concrete.Reset();
        for (var index = 0; index < operations; index++)
            _ = codec.Deserialize(in payload);
        return concrete.State;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RunDispatchProbeDeserializeUnusedConcrete(
        DispatchProbeCodec codec,
        in ReadOnlySequence<byte> payload,
        int operations)
    {
        codec.Reset();
        for (var index = 0; index < operations; index++)
            _ = codec.Deserialize(in payload);
        return codec.State;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RunDispatchProbeSerializeDualA(
        bool concretePath,
        IRpcCodec<int> codec,
        DispatchProbeCodec concrete,
        IBufferWriter<byte> writer,
        int operations)
    {
        concrete.Reset();
        var value = 42;
        if (concretePath)
        {
            for (var index = 0; index < operations; index++)
                concrete.Serialize(in value, writer);
        }
        else
        {
            for (var index = 0; index < operations; index++)
                codec.Serialize(in value, writer);
        }
        return concrete.State;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RunDispatchProbeSerializeDualB(
        bool concretePath,
        IRpcCodec<int> codec,
        DispatchProbeCodec concrete,
        IBufferWriter<byte> writer,
        int operations)
    {
        concrete.Reset();
        var value = 42;
        if (!concretePath)
        {
            for (var index = 0; index < operations; index++)
                codec.Serialize(in value, writer);
        }
        else
        {
            for (var index = 0; index < operations; index++)
                concrete.Serialize(in value, writer);
        }
        return concrete.State;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RunDispatchProbeDeserializeDualA(
        bool concretePath,
        IRpcCodec<int> codec,
        DispatchProbeCodec concrete,
        in ReadOnlySequence<byte> payload,
        int operations)
    {
        concrete.Reset();
        long checksum = 0;
        if (concretePath)
        {
            for (var index = 0; index < operations; index++)
                checksum += concrete.Deserialize(in payload);
        }
        else
        {
            for (var index = 0; index < operations; index++)
                checksum += codec.Deserialize(in payload);
        }
        return checksum ^ concrete.State;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RunDispatchProbeDeserializeDualB(
        bool concretePath,
        IRpcCodec<int> codec,
        DispatchProbeCodec concrete,
        in ReadOnlySequence<byte> payload,
        int operations)
    {
        concrete.Reset();
        long checksum = 0;
        if (!concretePath)
        {
            for (var index = 0; index < operations; index++)
                checksum += codec.Deserialize(in payload);
        }
        else
        {
            for (var index = 0; index < operations; index++)
                checksum += concrete.Deserialize(in payload);
        }
        return checksum ^ concrete.State;
    }

    private sealed class DispatchProbeCodec : IRpcCodec<int>
    {
        private int _state;

        internal int State => _state;

        internal void Reset() => _state = 17;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Serialize(in int value, IBufferWriter<byte> buffer)
        {
            _ = buffer;
            _state = unchecked((_state * 31) + value);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Deserialize(in ReadOnlySequence<byte> buffer)
        {
            _ = buffer;
            _state = unchecked((_state * 31) + 42);
            return _state;
        }
    }
}
