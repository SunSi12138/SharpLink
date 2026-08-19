using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Reflection;
using System.Threading;
using BenchmarkDotNet.Attributes;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

[MemoryDiagnoser]
public class PreCreditStreamingBenchmarks
{
    private const int PayloadBytes = 1024;
    private RpcSession _unsizedSession = null!;
    private RpcSession _sizedSession = null!;
    private BenchmarkTransport _unsizedTransport = null!;
    private BenchmarkTransport _sizedTransport = null!;
    private SharpLinkRuntimeContext _unsizedContext = null!;
    private SharpLinkRuntimeContext _sizedContext = null!;
    private readonly UnsizedPayload _unsizedPayload = new(PayloadBytes);
    private readonly SizedPayload _sizedPayload = new(PayloadBytes);

    [GlobalSetup]
    public void Setup()
    {
        var unsizedCodec = new UnsizedPayloadCodec();
        _unsizedContext = CreateContext(unsizedCodec);
        (_unsizedSession, _unsizedTransport) = CreateReadySession("pre-credit-bdn-unsized", _unsizedContext);

        var sizedCodec = new SizedPayloadCodec();
        _sizedContext = CreateContext(sizedCodec);
        (_sizedSession, _sizedTransport) = CreateReadySession("pre-credit-bdn-sized", _sizedContext);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        DisposeSession(_unsizedSession);
        DisposeSession(_sizedSession);
        _unsizedContext.Dispose();
        _sizedContext.Dispose();
        _unsizedTransport.Dispose();
        _sizedTransport.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void UnsizedFastConsumer()
    {
        CompleteSynchronously(_unsizedSession.SendStreamChunkAsync(1, 1, _unsizedPayload));
        _unsizedSession.ApplyWindowUpdate(1, new ProtocolV2WindowUpdate(1, PayloadBytes));
    }

    [Benchmark]
    public void ExactSizeControl()
    {
        CompleteSynchronously(_sizedSession.SendStreamChunkAsync(2, 1, _sizedPayload));
        _sizedSession.ApplyWindowUpdate(2, new ProtocolV2WindowUpdate(1, PayloadBytes));
    }

    private static SharpLinkRuntimeContext CreateContext<TCodec>(TCodec codec)
        where TCodec : class
    {
        var builder = new SharpLinkRuntimeContextBuilder();
        if (codec is IRpcCodec<UnsizedPayload> unsized)
            builder.AddCodec(unsized);
        if (codec is IRpcCodec<SizedPayload> sized)
            builder.AddCodec(sized);
        builder.Configure(options =>
        {
            options.FlowControl.MaxSendQueueBytes = 64 * 1024 * 1024;
            options.FlowControl.StreamReceiveWindowBytes = 16 * 1024 * 1024;
            options.FlowControl.ConnectionReceiveWindowBytes = 16 * 1024 * 1024;
        });
        return builder.Build(includeGeneratedAssemblyCatalog: false);
    }

    private static (RpcSession Session, BenchmarkTransport Transport) CreateReadySession(
        string id,
        SharpLinkRuntimeContext context)
    {
        var transport = new BenchmarkTransport(id);
        var session = new RpcSession(
            transport,
            new RpcSessionCreationOptions(RpcSessionRole.Client, context));
        var options = new NegotiatedSessionOptions(
            ProtocolV2Constants.MinorVersion,
            ProtocolV2Capabilities.FlowControl,
            context.Protocol.MaxFramePayloadBytes,
            16 * 1024 * 1024,
            16 * 1024 * 1024,
            CompressionBinding: null);
        if (!session.TryCompleteHandshake(options))
            throw new InvalidOperationException("Benchmark session handshake failed.");
        return (session, transport);
    }

    private static void CompleteSynchronously(ValueTask operation)
    {
        if (!operation.IsCompletedSuccessfully)
            operation.AsTask().GetAwaiter().GetResult();
        else
            operation.GetAwaiter().GetResult();
    }

    private static void DisposeSession(RpcSession session)
    {
        if (session is null)
            return;
        session.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

internal static class PreCreditStarvationEvidenceRunner
{
    private static readonly int[] PayloadSizes = [1024, 64 * 1024, 256 * 1024, 1024 * 1024];
    private static readonly int[] StreamCounts = [1, 8, 32, 128];

    internal static async Task RunAsync()
    {
        foreach (var payloadBytes in PayloadSizes)
        {
            foreach (var streams in StreamCounts)
                await RunCaseAsync(payloadBytes, streams).ConfigureAwait(false);
        }
    }

    private static async Task RunCaseAsync(int payloadBytes, int streams)
    {
        var codec = new UnsizedPayloadCodec();
        using var context = new SharpLinkRuntimeContextBuilder()
            .AddCodec(codec)
            .Build(includeGeneratedAssemblyCatalog: false);
        using var transport = new BenchmarkTransport($"pre-credit-starved-{payloadBytes}-{streams}");
        await using var session = new RpcSession(
            transport,
            new RpcSessionCreationOptions(RpcSessionRole.Client, context));
        var negotiated = new NegotiatedSessionOptions(
            ProtocolV2Constants.MinorVersion,
            ProtocolV2Capabilities.FlowControl,
            context.Protocol.MaxFramePayloadBytes,
            StreamReceiveWindowBytes: 1,
            ConnectionReceiveWindowBytes: 1,
            CompressionBinding: null);
        if (!session.TryCompleteHandshake(negotiated))
            throw new InvalidOperationException("Starvation evidence handshake failed.");

        // Consume the only protocol credit without materializing a stream-item writer.
        await session.AcquireStreamSendCreditAsync(900_000, 1, 1, CancellationToken.None)
            .ConfigureAwait(false);

        var sends = new Task[streams];
        for (var index = 0; index < sends.Length; index++)
        {
            sends[index] = session.SendStreamChunkAsync(
                index + 1,
                1,
                new UnsizedPayload(payloadBytes),
                CancellationToken.None).AsTask();
        }

        await WaitForStableSerializeCountAsync(codec, sends).ConfigureAwait(false);

        var reservedBytes = ReadInternalNumber(session, "PreCreditSerializedBytes");
        var byteLimit = ReadInternalNumber(session, "PreCreditSerializedByteLimit");
        var waiterCount = ReadInternalNumber(session, "PreCreditSerializedWaiterCount");
        Console.WriteLine(
            $"[PreCreditStarvation] payloadBytes={payloadBytes} streams={streams} " +
            $"serializeCount={codec.SerializeCount} reservedBytes={reservedBytes} " +
            $"byteLimit={byteLimit} waiterCount={waiterCount}");

        var terminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "starvation evidence cleanup");
        session.NotifyDisconnected(terminal);
        for (var index = 0; index < sends.Length; index++)
        {
            try
            {
                await sends[index].ConfigureAwait(false);
            }
            catch (Exception exception) when (ReferenceEquals(exception, terminal))
            {
            }
        }
    }

    private static async Task WaitForStableSerializeCountAsync(
        UnsizedPayloadCodec codec,
        Task[] sends)
    {
        var previous = -1;
        var stableRounds = 0;
        for (var round = 0; round < 20_000; round++)
        {
            var current = codec.SerializeCount;
            if (current == previous)
            {
                stableRounds++;
                if (stableRounds >= 64)
                    return;
            }
            else
            {
                previous = current;
                stableRounds = 0;
            }

            if (Array.TrueForAll(sends, static task => task.IsCompleted))
                return;
            await Task.Yield();
        }
        throw new InvalidOperationException("Starvation evidence did not reach a stable serialized-owner count.");
    }

    private static string ReadInternalNumber(RpcSession session, string propertyName)
    {
        var property = typeof(RpcSession).GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        return property?.GetValue(session)?.ToString() ?? "n/a";
    }
}

internal readonly record struct UnsizedPayload(int Bytes);

internal sealed class UnsizedPayloadCodec : IRpcCodec<UnsizedPayload>
{
    private int _serializeCount;
    internal int SerializeCount => Volatile.Read(ref _serializeCount);

    public void Serialize(in UnsizedPayload value, IBufferWriter<byte> buffer)
    {
        Interlocked.Increment(ref _serializeCount);
        var remaining = value.Bytes;
        while (remaining != 0)
        {
            var span = buffer.GetSpan(remaining);
            var count = Math.Min(span.Length, remaining);
            span[..count].Fill(0x5a);
            buffer.Advance(count);
            remaining -= count;
        }
    }

    public UnsizedPayload Deserialize(in ReadOnlySequence<byte> buffer)
        => new(checked((int)buffer.Length));
}

internal readonly record struct SizedPayload(int Bytes);

internal sealed class SizedPayloadCodec : IRpcCodec<SizedPayload>, IRpcSizedCodec<SizedPayload>
{
    public bool CanExactSize => true;

    public void Serialize(in SizedPayload value, IBufferWriter<byte> buffer)
        => Write(value.Bytes, buffer);

    public SizedPayload Deserialize(in ReadOnlySequence<byte> buffer)
        => new(checked((int)buffer.Length));

    public bool TryGetEncodedSize(in SizedPayload value, out int size)
    {
        size = value.Bytes;
        return true;
    }

    public bool TryGetEncodedSize(
        in SizedPayload value,
        out int size,
        out IRpcSizedCodecSnapshot? snapshot)
    {
        size = value.Bytes;
        snapshot = null;
        return true;
    }

    public void SerializeSized(
        in SizedPayload value,
        IBufferWriter<byte> buffer,
        int size,
        IRpcSizedCodecSnapshot? snapshot)
    {
        if (size != value.Bytes || snapshot is not null)
            throw new InvalidOperationException("Sized benchmark codec received an invalid exact-size contract.");
        Write(value.Bytes, buffer);
    }

    public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
    {
        if (snapshot is not null)
            throw new InvalidOperationException("Sized benchmark codec does not own snapshots.");
    }

    private static void Write(int bytes, IBufferWriter<byte> buffer)
    {
        var span = buffer.GetSpan(bytes);
        span[..bytes].Fill(0x33);
        buffer.Advance(bytes);
    }
}

internal sealed class BenchmarkTransport : ITransportConnection, IDisposable
{
    private readonly PipeReader _input = PipeReader.Create(Stream.Null);
    private readonly PipeWriter _output = PipeWriter.Create(
        Stream.Null,
        new StreamPipeWriterOptions(leaveOpen: true));

    internal BenchmarkTransport(string id) => Id = id;

    public string Id { get; }
    public PipeReader Input => _input;
    public PipeWriter Output => _output;
    public EndPoint? LocalEndPoint => null;
    public EndPoint? RemoteEndPoint => null;

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _output.Complete();
        _input.Complete();
    }
}
