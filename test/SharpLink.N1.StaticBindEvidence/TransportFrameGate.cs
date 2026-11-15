using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.N1.StaticBindEvidence;

internal sealed record ObservedFrame(string SessionId, bool Outbound, ProtocolV2FrameHeader Header, byte[] Payload);
/// <summary>A finite fixture-only transport gate. It forwards actual product bytes and never manufactures an Ack.</summary>
internal sealed class TransportFrameGate(ProtocolV2FrameType type, bool afterFlush)
{
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly List<ObservedFrame> _frames = [];
    private int _claimed;
    internal ITransportConnection? Connection;
    internal bool AfterFlush => afterFlush;
    internal IReadOnlyList<ObservedFrame> Frames { get { lock (_gate) return _frames.ToArray(); } }
    internal bool Observe(string sessionId, bool outbound, ProtocolV2FrameHeader header, ReadOnlySequence<byte> payload)
    {
        lock (_gate)
        {
            if (_frames.Count >= 512) throw new InvalidOperationException("FIXTURE frame inventory exceeded 512.");
            _frames.Add(new(sessionId, outbound, header, payload.ToArray()));
        }
        return outbound && header.Type == type && Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;
    }
    internal async Task WaitAsync(CancellationToken cancellationToken)
    { Entered.TrySetResult(); await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
    internal void Release() => _release.TrySetResult();
}
internal sealed class GatedPipeWriter(PipeWriter inner, TransportFrameGate gate, string sessionId) : PipeWriter
{
    private Memory<byte> _last;
    private readonly ArrayBufferWriter<byte> _observed = new();
    private bool _gateThisFlush;
    public override void Advance(int bytes)
    {
        _last.Span[..bytes].CopyTo(_observed.GetSpan(bytes)); _observed.Advance(bytes);
        inner.Advance(bytes);
    }
    public override Memory<byte> GetMemory(int sizeHint = 0) => _last = inner.GetMemory(sizeHint);
    public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    public override void CancelPendingFlush() => inner.CancelPendingFlush();
    public override void Complete(Exception? exception = null) { gate.Release(); inner.Complete(exception); }
    public override ValueTask CompleteAsync(Exception? exception = null) { gate.Release(); return inner.CompleteAsync(exception); }
    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        var sequence = new ReadOnlySequence<byte>(_observed.WrittenMemory);
        while (ProtocolV2FrameParser.TryReadFrame(ref sequence, new SharpLinkProtocolOptions(), out var header, out var payload))
            _gateThisFlush |= gate.Observe(sessionId, true, header, payload);
        var remaining = sequence.ToArray(); _observed.Clear(); remaining.CopyTo(_observed.GetSpan(remaining.Length)); _observed.Advance(remaining.Length);
        return FlushCoreAsync(cancellationToken);
    }
    private async ValueTask<FlushResult> FlushCoreAsync(CancellationToken cancellationToken)
    {
        var shouldGate = _gateThisFlush; _gateThisFlush = false;
        if (shouldGate && !gate.AfterFlush) await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var result = await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (shouldGate && gate.AfterFlush) await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }
}
internal sealed class GatedConnection : ITransportConnection
{
    private readonly ITransportConnection _inner;
    private readonly TransportFrameGate _gate;
    internal GatedConnection(ITransportConnection inner, TransportFrameGate gate)
    { _inner = inner; _gate = gate; Output = new GatedPipeWriter(inner.Output, gate, inner.Id); Input = new RecordingPipeReader(inner.Input, gate, inner.Id); }
    public string Id => _inner.Id;
    public PipeReader Input { get; }
    public PipeWriter Output { get; }
    public EndPoint? LocalEndPoint => _inner.LocalEndPoint;
    public EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;
    public ValueTask DisposeAsync() { _gate.Release(); return _inner.DisposeAsync(); }
}
internal sealed class GatedClientFactory(IClientTransportFactory inner, TransportFrameGate gate) : IClientTransportFactory
{
    internal ITransportConnection? Connection;
    public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
    { var connection = new GatedConnection(await inner.ConnectAsync(cancellationToken).ConfigureAwait(false), gate); Connection = connection; gate.Connection = connection; return connection; }
    public ValueTask DisposeAsync() { gate.Release(); return inner.DisposeAsync(); }
}
internal sealed class GatedListener(IServerTransportListener inner, TransportFrameGate gate) : IServerTransportListener
{
    public EndPoint? LocalEndPoint => inner.LocalEndPoint;
    public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
        => new GatedConnection(await inner.AcceptAsync(cancellationToken).ConfigureAwait(false), gate);
    public ValueTask DisposeAsync() { gate.Release(); return inner.DisposeAsync(); }
}

internal sealed class RecordingPipeReader(PipeReader inner, TransportFrameGate gate, string sessionId) : PipeReader
{
    private ReadOnlySequence<byte> _current;
    public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    { var result = await inner.ReadAsync(cancellationToken).ConfigureAwait(false); _current = result.Buffer; return result; }
    public override bool TryRead(out ReadResult result)
    { if (!inner.TryRead(out result)) return false; _current = result.Buffer; return true; }
    public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);
    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        var sequence = _current.Slice(0, consumed);
        while (ProtocolV2FrameParser.TryReadFrame(ref sequence, new SharpLinkProtocolOptions(), out var header, out var payload))
            _ = gate.Observe(sessionId, false, header, payload);
        inner.AdvanceTo(consumed, examined); _current = default;
    }
    public override void CancelPendingRead() => inner.CancelPendingRead();
    public override void Complete(Exception? exception = null) => inner.Complete(exception);
    public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
}
