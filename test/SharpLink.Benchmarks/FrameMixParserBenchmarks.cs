using System;
using System.Buffers;
using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

/// <summary>
/// Isolates protocol parsing and header-validation cost from the loopback RPC
/// benchmarks. The mixed buffer has the same frame count as the unary buffer.
/// </summary>
[MemoryDiagnoser]
[AllStatisticsColumn]
public class FrameMixParserBenchmarks
{
    private const int FramesPerOperation = 100;
    private readonly SharpLinkProtocolOptions _limits = new();
    private ReadOnlyMemory<byte> _mixedFrames;
    private ReadOnlyMemory<byte> _unaryFrames;

    [GlobalSetup]
    public void Setup()
    {
        _unaryFrames = CreateUnaryFrames();
        _mixedFrames = CreateMixedFrames();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = FramesPerOperation)]
    public int ContinuousUnaryRequests() => ParseAll(_unaryFrames);

    [Benchmark(OperationsPerInvoke = FramesPerOperation)]
    public int MixedRequestAndControlFrames() => ParseAll(_mixedFrames);

    private int ParseAll(ReadOnlyMemory<byte> frames)
    {
        var buffer = new ReadOnlySequence<byte>(frames);
        var count = 0;
        while (ProtocolV2FrameParser.TryReadFrame(
                   ref buffer,
                   _limits,
                   out _,
                   out _))
        {
            count++;
        }
        if (!buffer.IsEmpty || count != FramesPerOperation)
            throw new InvalidOperationException($"Parsed {count} frames with {buffer.Length} bytes left over.");
        return count;
    }

    private static ReadOnlyMemory<byte> CreateUnaryFrames()
    {
        using var writer = new PooledByteBufferWriter(4 * 1024);
        for (ulong requestId = 1; requestId <= FramesPerOperation; requestId++)
            WriteRequest(writer, requestId, ProtocolV2FrameFlags.HasReturn);
        return writer.WrittenMemory.ToArray();
    }

    private static ReadOnlyMemory<byte> CreateMixedFrames()
    {
        using var writer = new PooledByteBufferWriter(4 * 1024);
        ulong requestId = 1;
        for (var index = 0; index < 90; index++)
            WriteRequest(writer, requestId++, ProtocolV2FrameFlags.HasReturn);
        WriteRequest(writer, requestId++, ProtocolV2FrameFlags.OneWay);
        WriteRequest(writer, requestId++, ProtocolV2FrameFlags.OneWay);
        WriteStreamData(writer, requestId++);
        WriteStreamData(writer, requestId++);
        WriteStreamComplete(writer, requestId++);
        WriteCancel(writer, requestId++);
        WriteHeartbeat(writer, ProtocolV2FrameType.Ping);
        WriteHeartbeat(writer, ProtocolV2FrameType.Pong);
        WriteErrorResponse(writer, requestId++);
        WriteWindowUpdate(writer, requestId);
        return writer.WrittenMemory.ToArray();
    }

    private static void WriteRequest(
        PooledByteBufferWriter writer,
        ulong requestId,
        ProtocolV2FrameFlags flags)
    {
        var token = ProtocolV2FrameWriter.BeginFrame(
            writer,
            ProtocolV2FrameType.Request,
            flags,
            requestId);
        var prefix = writer.GetSpan(ProtocolV2Constants.RequestPrefixBytes);
        BinaryPrimitives.WriteInt64LittleEndian(prefix, 11);
        BinaryPrimitives.WriteInt64LittleEndian(prefix[sizeof(long)..], 22);
        writer.Advance(ProtocolV2Constants.RequestPrefixBytes);
        ProtocolV2FrameWriter.EndFrame(writer, token);
    }

    private static void WriteStreamData(PooledByteBufferWriter writer, ulong requestId)
    {
        var token = ProtocolV2FrameWriter.BeginFrame(
            writer,
            ProtocolV2FrameType.StreamData,
            ProtocolV2FrameFlags.None,
            requestId);
        var payload = writer.GetSpan(sizeof(ushort) + 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 1);
        payload[sizeof(ushort)] = 42;
        writer.Advance(sizeof(ushort) + 1);
        ProtocolV2FrameWriter.EndFrame(writer, token);
    }

    private static void WriteStreamComplete(PooledByteBufferWriter writer, ulong requestId)
    {
        var token = ProtocolV2FrameWriter.BeginFrame(
            writer,
            ProtocolV2FrameType.StreamComplete,
            ProtocolV2FrameFlags.None,
            requestId);
        var payload = writer.GetSpan(sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 1);
        writer.Advance(sizeof(ushort));
        ProtocolV2FrameWriter.EndFrame(writer, token);
    }

    private static void WriteCancel(PooledByteBufferWriter writer, ulong requestId)
    {
        var token = ProtocolV2FrameWriter.BeginFrame(
            writer,
            ProtocolV2FrameType.Cancel,
            ProtocolV2FrameFlags.None,
            requestId);
        ProtocolV2PayloadCodec.WriteCancelReason(
            writer,
            ProtocolV2CancelReason.UserCancellation);
        ProtocolV2FrameWriter.EndFrame(writer, token);
    }

    private static void WriteHeartbeat(
        PooledByteBufferWriter writer,
        ProtocolV2FrameType type)
    {
        var token = ProtocolV2FrameWriter.BeginFrame(
            writer,
            type,
            ProtocolV2FrameFlags.None,
            requestId: 0);
        var payload = writer.GetSpan(sizeof(long));
        BinaryPrimitives.WriteInt64LittleEndian(payload, 1);
        writer.Advance(sizeof(long));
        ProtocolV2FrameWriter.EndFrame(writer, token);
    }

    private static void WriteErrorResponse(PooledByteBufferWriter writer, ulong requestId)
    {
        var token = ProtocolV2FrameWriter.BeginFrame(
            writer,
            ProtocolV2FrameType.Response,
            ProtocolV2FrameFlags.Error,
            requestId);
        ProtocolV2PayloadCodec.WriteError(
            writer,
            SharpLinkErrorCode.Internal,
            "rare benchmark error",
            maxMessageBytes: 256,
            out _);
        ProtocolV2FrameWriter.EndFrame(writer, token);
    }

    private static void WriteWindowUpdate(PooledByteBufferWriter writer, ulong requestId)
    {
        var token = ProtocolV2FrameWriter.BeginFrame(
            writer,
            ProtocolV2FrameType.WindowUpdate,
            ProtocolV2FrameFlags.None,
            requestId);
        ProtocolV2PayloadCodec.WriteWindowUpdate(
            writer,
            new ProtocolV2WindowUpdate(StreamId: 1, Credit: 32));
        ProtocolV2FrameWriter.EndFrame(writer, token);
    }
}
