using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using SharpLink.Abstractions;
using SharpLink.FlowStatePhaseB;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal sealed partial class PhaseBTransportCase : IAsyncDisposable
{
    private const int StreamWindow = 8192;
    private readonly SharpLinkRuntimeContext _context;
    private readonly RpcSession _sender;
    private readonly RpcSession _receiver;
    private readonly StreamFlowController? _phaseA;
    private readonly GrantAuthority? _grants;
#if SHARPLINK_READY_WRITER_EXPERIMENT
    private readonly ReadyWriterCoordinator? _readyWriter;
    private readonly bool _allocationDiagnostic = Environment.GetEnvironmentVariable("SHARPLINK_READY_ALLOCATION_DIAGNOSTIC") == "1";
    private readonly long[] _preparationAllocated;
#endif
    private readonly int _slots, _flushBytes;
    private readonly int _streams, _items, _bytes, _connectionWindow;
    private readonly string _mode, _transport;
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(45));
    private readonly TaskCompletionSource _start = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _received, _returned, _updates;
    private readonly long[] _durationTicks;
    private readonly GrantAuthority.Lease[] _leases;
    private readonly ValidatingConsumer[] _consumers;
    private Task[] _ownedWork = [];
    private readonly PhaseBTransportFailure _failures;

    private PhaseBTransportCase(string mode, string transport, ITransportConnection sender, ITransportConnection receiver,
        int streams, int items, int bytes, int connectionWindow, int slots, int flushBytes, int quantum, int preparedByteBudget)
    {
        _failures = new PhaseBTransportFailure(_timeout);
        _mode = mode; _transport = transport; _streams = streams; _items = items; _bytes = bytes; _connectionWindow = connectionWindow;
        _slots = slots; _flushBytes = flushBytes;
#if SHARPLINK_READY_WRITER_EXPERIMENT
        _preparationAllocated = new long[streams];
#endif
        _durationTicks = new long[streams]; _leases = new GrantAuthority.Lease[streams]; _consumers = new ValidatingConsumer[streams];
        _context = new SharpLinkRuntimeContextBuilder().Configure(o =>
        {
            o.FlowControl.StreamReceiveWindowBytes = StreamWindow;
            o.FlowControl.ConnectionReceiveWindowBytes = connectionWindow;
        }).Build(includeGeneratedAssemblyCatalog: false);
        var flush = new RpcSessionFlushOptions(flushBytes, TimeSpan.MaxValue);
        _sender = new RpcSession(sender, new RpcSessionCreationOptions(RpcSessionRole.Client, _context, flush));
        _receiver = new RpcSession(receiver, new RpcSessionCreationOptions(RpcSessionRole.Server, _context, flush));
        _sender.OnDisconnected += OnSenderDisconnected;
        _receiver.OnDisconnected += OnReceiverDisconnected;
        // The send controller is external in ALL variants for an identical session path.
        // The receiver is the unchanged negotiated production controller in ALL variants.
        Handshake(_sender, ProtocolV2Capabilities.None);
        Handshake(_receiver, ProtocolV2Capabilities.FlowControl);
#if SHARPLINK_READY_WRITER_EXPERIMENT
        if (mode is "A-ready" or "B3-ready")
            _readyWriter = new ReadyWriterCoordinator(_sender, _context, _timeout, mode == "A-ready", streams, items, bytes, StreamWindow, connectionWindow, slots, quantum, preparedByteBudget);
        else
#endif
        if (mode == "A") _phaseA = new StreamFlowController(StreamWindow, connectionWindow, _context.Protocol.MaxFramePayloadBytes, streams);
        else _grants = new GrantAuthority(StreamWindow, connectionWindow, mode == "B1" ? 0 : 4096, streams, adaptiveGrants: mode == "B2-adaptive");
        for (var i = 0; i < streams; i++)
        {
            _consumers[i] = new ValidatingConsumer(bytes);
            _receiver.StreamManager.Register(i + 1, 1, _consumers[i]);
        }
    }

    private void Handshake(RpcSession session, ProtocolV2Capabilities capabilities)
    {
        if (!session.TryCompleteHandshake(new NegotiatedSessionOptions(ProtocolV2Constants.MinorVersion, capabilities,
                _context.Protocol.MaxFramePayloadBytes, StreamWindow, _connectionWindow, null)))
            throw new InvalidOperationException("Could not set identical session parameters.");
    }

    internal static async Task<PhaseBTransportEvidenceRunner.Sample> RunAsync(string mode, string transport,
        int streams, int items, int bytes, int round, int connectionWindow, int slots = 16, int flushBytes = 1, int quantum = 1, int preparedByteBudget = -1)
    {
        if (preparedByteBudget < 0) preparedByteBudget = int.Parse(Environment.GetEnvironmentVariable("SHARPLINK_READY_PREPARED_BYTES") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        var (sender, receiver) = await PhaseBTransportPair.CreateAsync(transport);
        var test = new PhaseBTransportCase(mode, transport, sender, receiver, streams, items, bytes, connectionWindow, slots, flushBytes, quantum, preparedByteBudget);
#if SHARPLINK_READY_WRITER_DIAGNOSTIC
        using var diagnostic = new Timer(_ => test.DumpForStall(), null, 5000, 5000);
#endif
        return await PhaseBTransportFailure.RunCaseAsync(() => test.MeasureAsync(round),
            () => test.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)), error =>
            {
                Console.Error.WriteLine($"CASE STATE origin={test._failures.Origin}; senderConnected={test._sender.IsConnected}; receiverConnected={test._receiver.IsConnected}; unfinished=[{string.Join(",", test._ownedWork.Select((task, index) => (task, index)).Where(x => !x.task.IsCompleted).Select(x => $"{x.index}:{x.task.Status}"))}]");
                Console.Error.WriteLine($"FAILED {mode}/{transport}/c{streams}/r{round}: received={test._received}, credits={test._returned}, updates={test._updates}: {error}");
            });
    }

    private async Task<PhaseBTransportEvidenceRunner.Sample> MeasureAsync(int round)
    {
        if (_grants is not null)
            for (var i = 0; i < _streams; i++) _leases[i] = await _grants.OpenAsync(i + 1, 1);
#if SHARPLINK_READY_WRITER_EXPERIMENT
        _readyWriter?.Attach();
#endif
        var receives = ReceiveAsync();
        var returns = ReturnCreditsAsync();
        var producers = Enumerable.Range(0, _streams).Select(ProduceAsync).ToArray();
        // Any parser/producer fault cancels every blocked participant and is retained as a failed report.
        var work = producers.Concat(new[] { receives, returns }).ToArray();
#if SHARPLINK_READY_WRITER_EXPERIMENT
        if (_readyWriter is not null) work = work.Append(_readyWriter.Completion).ToArray();
#endif
        _ownedWork = work.Select((task, index) => _failures.ObserveAsync(task,
            index < _streams ? $"producer-{index}" : index == _streams ? "receive" : index == _streams + 1 ? "credit-return" : "ready-writer")).ToArray();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var process = Process.GetCurrentProcess();
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var cpu = process.TotalProcessorTime;
        var commands = _grants?.QueueSubmissions ?? 0;
        var started = Stopwatch.GetTimestamp();
        _start.TrySetResult();
        await _failures.WaitAsync(_ownedWork);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        var totalAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        var ownerCommands = (_grants?.QueueSubmissions ?? 0) - commands;
        var total = checked((long)_streams * _items);
        if (_received != total || _returned != total * _bytes || _consumers.Any(c => c.Count != _items))
            throw new InvalidOperationException("An item or peer credit was lost.");
        if (_grants is not null)
        {
            var ledger = await _grants.SnapshotAsync();
            if (ledger.Pending != 0 || ledger.Outstanding != 0 || ledger.Publications != 0 || ledger.Waiters != 0 || ledger.Free + ledger.Unspent != _connectionWindow)
                throw new InvalidOperationException("B2 did not settle all data, publications and credit.");
            foreach (var lease in _leases) await _grants.CloseAsync(lease);
            var closed = await _grants.SnapshotAsync();
            if (closed.Retained != 0 || closed.Free != _connectionWindow)
                throw new InvalidOperationException("Closed grants did not return the full window.");
        }
        else if (_phaseA is not null)
        {
            // All credit-return tasks completed before these lifecycle calls.
            for (var i = 0; i < _streams; i++) _phaseA!.CompleteSendStream(i + 1, 1);
        }
        var durations = _durationTicks.Select(t => t * 1000.0 / Stopwatch.Frequency).ToArray();
        var result = new PhaseBTransportEvidenceRunner.Sample(_mode, _transport, _streams, _items, _bytes, round, StreamWindow, _connectionWindow,
            _mode.StartsWith("B2", StringComparison.Ordinal) ? 4096 : 0, _received, _returned, _updates, ownerCommands,
            _grants?.AcquireSubmissions ?? 0, _grants?.Revocations ?? 0, _grants?.RevocationSweeps ?? 0, _grants?.AdmittedWaiters ?? 0,
            elapsed.TotalMilliseconds, cpuMs, total / elapsed.TotalSeconds, totalAllocated / (double)total,
            ownerCommands / (double)total, durations.Max() - durations.Min(), durations,
            Environment.GetEnvironmentVariable("SHARPLINK_SOURCE_TREE") ?? "unrecorded",
            "balanced-wire/one-unsettled-emission; not production RPC or duplicate-credit compatibility");
#if SHARPLINK_READY_WRITER_EXPERIMENT
        if (_readyWriter is not null) result = result with
        {
            ReadyWriterMetrics = _readyWriter.Metrics(),
            ProtocolScope = $"balanced-wire/bounded-{_slots}-frame-producer-ring/same-SendPump/flush-{_flushBytes}; not full RPC/lifecycle compatibility",
        };
        if (_readyWriter is not null)
        {
            result.ReadyWriterMetrics!["AllocationDiagnostic"] = _allocationDiagnostic ? 1 : 0;
            if (_allocationDiagnostic) result.ReadyWriterMetrics!["ProducerPreparationAllocatedBytes"] = _preparationAllocated.Sum();
        }
#endif
        return result;
    }

    private void OnSenderDisconnected(Exception? error)
        => _failures.RecordAndCancel("sender-session", error ?? new IOException("Sender session closed."));

    private void OnReceiverDisconnected(Exception? error)
        => _failures.RecordAndCancel("receiver-session", error ?? new IOException("Receiver session closed."));

    private async Task ProduceAsync(int index)
    {
        await _start.Task;
        var started = Stopwatch.GetTimestamp();
        StreamFlowController.ResolvedSendCreditLease phaseLease = default;
        for (var item = 0; item < _items; item++)
        {
#if SHARPLINK_READY_WRITER_EXPERIMENT
            var beforePreparation = _allocationDiagnostic ? GC.GetAllocatedBytesForCurrentThread() : 0;
#endif
            var packet = _context.Buffers.Rent();
            using (packet.BeginPacketScope(ProtocolV2FrameType.StreamData, ProtocolV2FrameFlags.None, (ulong)(index + 1)))
            {
                var span = packet.GetSpan(_bytes + 2)[..(_bytes + 2)];
                span.Fill(0xA5);
                BinaryPrimitives.WriteUInt16LittleEndian(span, 1);
                BinaryPrimitives.WriteInt32LittleEndian(span[2..], item);
                packet.Advance(_bytes + 2);
            }
            packet = _sender.PrepareOutboundFrame(packet, _timeout.Token);
#if SHARPLINK_READY_WRITER_EXPERIMENT
            if (_allocationDiagnostic) _preparationAllocated[index] += GC.GetAllocatedBytesForCurrentThread() - beforePreparation;
            if (_readyWriter is not null)
            {
                await _readyWriter.EnqueueAsync(index, packet);
                continue;
            }
#endif
            GrantAuthority.Publication publication = default;
            try
            {
                if (_phaseA is not null)
                {
                    if (phaseLease.IsResolved) await _phaseA.AcquireSendCreditAsync(in phaseLease, _bytes, _timeout.Token);
                    else phaseLease = await _phaseA.AcquireSendCreditLeaseAsync(index + 1, 1, _bytes, _timeout.Token);
                }
                else publication = await _grants!.AcquirePublicationAsync(_leases[index], _bytes, _timeout.Token);
            }
            catch { _context.Buffers.Return(packet); throw; }
            var accepted = _sender.TryEnqueuePreparedFrame(packet, observeEmission: true, CancellationToken.None,
                failureObserver: null, out var emission, out var rejection);
            if (!accepted)
            {
                if (_phaseA is not null) _phaseA.ReturnUnsentCredit(in phaseLease, _bytes);
                else await _grants!.FinishPublicationAsync(publication, accepted: false);
                throw rejection ?? new InvalidOperationException("Queue rejected a measured frame.");
            }
            try { await emission; }
            finally
            {
                // An accepted frame may already be visible even when emission fails.
                if (_grants is not null) await _grants.FinishPublicationAsync(publication, accepted: true);
            }
        }
        _durationTicks[index] = Stopwatch.GetTimestamp() - started;
    }

    private async Task ReceiveAsync()
    {
        await _start.Task;
        while (_received < (long)_streams * _items)
        {
            var read = await _receiver.Input.ReadAsync(_timeout.Token);
            var remaining = read.Buffer;
            try
            {
                while (ProtocolV2FrameParser.TryReadFrame(ref remaining, _context.Protocol, out var header, out var payload))
                {
                    if (header.Type != ProtocolV2FrameType.StreamData || payload.Length != _bytes + 2 ||
                        header.RequestId < 1 || header.RequestId > (ulong)_streams)
                        throw new InvalidDataException("Unexpected data frame.");
                    var streamId = ReadStreamId(payload);
                    if (streamId != 1) throw new InvalidDataException("Wrong stream identity.");
                    var requestId = checked((long)header.RequestId);
                    await _receiver.StreamManager.DispatchChunkAsync(requestId, streamId, payload.Slice(2));
                    _received++;
                    if (_consumers[requestId - 1].Count == _items)
                        _receiver.StreamManager.CompleteStream(requestId, streamId, (Exception?)null);
                }
                if (read.IsCompleted && _received != (long)_streams * _items) throw new EndOfStreamException();
            }
            finally { _receiver.Input.AdvanceTo(remaining.Start, read.Buffer.End); }
        }
        await _receiver.FlushSendQueueAsync();
    }

    private static ushort ReadStreamId(ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out short bits)) throw new InvalidDataException("Missing stream id.");
        return unchecked((ushort)bits);
    }

    private async Task ReturnCreditsAsync()
    {
        await _start.Task;
        var expected = checked((long)_streams * _items * _bytes);
        while (_returned < expected)
        {
            var read = await _sender.Input.ReadAsync(_timeout.Token);
            var remaining = read.Buffer;
            try
            {
                while (ProtocolV2FrameParser.TryReadFrame(ref remaining, _context.Protocol, out var header, out var payload))
                {
                    if (header.Type != ProtocolV2FrameType.WindowUpdate) throw new InvalidDataException("Unexpected credit frame.");
                    var update = ProtocolV2PayloadCodec.ReadWindowUpdate(payload);
                    var requestId = checked((long)header.RequestId);
                    if (update.StreamId != 1 || requestId < 1 || requestId > _streams) throw new InvalidDataException("Wrong credit identity.");
                    var bytes = checked((int)update.Credit);
#if SHARPLINK_READY_WRITER_EXPERIMENT
                    if (_readyWriter is not null) await _readyWriter.EnqueueUpdateAsync(requestId, update.StreamId, bytes);
                    else
#endif
                    if (_phaseA is not null) _phaseA.ApplyWindowUpdate(requestId, update.StreamId, bytes);
                    else
                    {
                        var observed = await _grants!.ObserveWindowUpdateAsync(requestId, update.StreamId, bytes);
                        if (!observed.Matched || observed.Returned != bytes || observed.Excess != 0)
                            throw new InvalidDataException("This balanced-wire control encountered incompatible excess credit.");
                    }
                    _updates++; _returned += bytes;
                    if (_returned > expected) throw new InvalidDataException("Duplicate credit in balanced measurement.");
                }
                if (read.IsCompleted && _returned != expected) throw new EndOfStreamException();
            }
            finally { _sender.Input.AdvanceTo(remaining.Start, read.Buffer.End); }
        }
    }

    private sealed class ValidatingConsumer(int bytes) : IStreamConsumptionAwareDispatcher
    {
        private Action<long, ushort, int>? _consumed;
        private long _request;
        private ushort _stream;
        internal int Count;
        public void SetBytesConsumedCallback(Action<long, ushort, int>? callback, long requestId, ushort streamId)
        { _consumed = callback; _request = requestId; _stream = streamId; }
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => DispatchAsync(payload, checked((int)payload.Length));
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload, int encodedByteCount)
        {
            var reader = new SequenceReader<byte>(payload);
            if (encodedByteCount != bytes || payload.Length != bytes || !reader.TryReadLittleEndian(out int sequence) || sequence != Count)
                throw new InvalidDataException("Missing, duplicate or corrupt data sequence.");
            while (reader.TryRead(out var value)) if (value != 0xA5) throw new InvalidDataException("Corrupt payload.");
            Count++;
            _consumed?.Invoke(_request, _stream, encodedByteCount);
            return ValueTask.CompletedTask;
        }
        public void Complete(bool isError, string? message) { if (isError) throw new InvalidDataException(message); }
        public void Complete(Exception? exception) { if (exception is not null) throw exception; }
    }

    public async ValueTask DisposeAsync()
    {
        // Normal teardown is not a new measured failure.
        _sender.OnDisconnected -= OnSenderDisconnected;
        _receiver.OnDisconnected -= OnReceiverDisconnected;
        _timeout.Cancel();
        try { await _sender.DisposeAsync(); }
        finally
        {
            try { await _receiver.DisposeAsync(); }
            finally
            {
                if (_grants is not null) await _grants.DisposeAsync();
                // Sender stop settles ready-writer ownership; canceled producers then
                // return their prepared buffers BEFORE context/pool disposal.
                try { await Task.WhenAll(_ownedWork).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception error) when (error is not TimeoutException) { Console.Error.WriteLine($"Joined failed transport work: {error.GetType().Name}"); }
                _context.Dispose(); _timeout.Dispose();
            }
        }
    }
}
