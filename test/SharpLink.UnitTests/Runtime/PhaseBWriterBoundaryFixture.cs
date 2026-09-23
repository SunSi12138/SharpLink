using System.Buffers.Binary;
using System.IO.Pipelines;
using SharpLink.FlowStatePhaseB;

namespace SharpLink.UnitTests.Runtime;

// Experimental boundary fixture only. The grant model gates prepared StreamData
// before the real RpcSession/SendPump API. This does not replace RpcSession's
// controller or emulate peer WindowUpdate routing and logical-call arbitration.
internal sealed class PhaseBWriterBoundaryFixture : IAsyncDisposable
{
    private readonly Pipe _input = new();
    private readonly Pipe _output = new();
    private readonly SharpLinkRuntimeContext _context;
    private readonly List<Ticket> _tickets = [];
    internal readonly PausedFlushWriter Writer;
    internal readonly RpcSession Session;
    internal readonly GrantAuthority Credits;
    internal IRpcByteBufferWriter? LastPacket;

    internal PhaseBWriterBoundaryFixture(int window = 64, int sendQueueBytes = 1024 * 1024)
    {
        _context = new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.FlowControl.MaxSendQueueBytes = sendQueueBytes)
            .Build(includeGeneratedAssemblyCatalog: false);
        Writer = new PausedFlushWriter(_output.Writer);
        Session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "phase-b-writer-boundary", _input.Reader, Writer,
            RpcSessionTestFixture.ClientOptions(_context, new RpcSessionFlushOptions(1, TimeSpan.MaxValue)));
        // The real session's built-in flow controller is deliberately not negotiated:
        // only this external prototype ledger accounts for these test frames.
        Credits = new GrantAuthority(window, window, window);
    }

    internal async Task<Ticket> EnqueueAsync(GrantAuthority.Lease lease, int payloadBytes,
        CancellationToken cancellationToken = default)
    {
        var packet = _context.Buffers.Rent();
        LastPacket = packet;
        using (packet.BeginPacketScope(ProtocolV2FrameType.StreamData,
                   ProtocolV2FrameFlags.None, unchecked((ulong)lease.State.Key)))
        {
            var id = packet.GetSpan(sizeof(ushort));
            BinaryPrimitives.WriteUInt16LittleEndian(id, 1);
            packet.Advance(sizeof(ushort));
            packet.Write(new byte[payloadBytes]);
        }
        // This helper returns the packet itself when preparation fails.
        packet = Session.PrepareOutboundFrame(packet, cancellationToken);
        var expectedWire = packet.WrittenMemory.ToArray();
        GrantAuthority.Publication publication;
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                   cancellationToken, Session.LifetimeToken))
        {
            try { publication = await Credits.AcquirePublicationAsync(lease, payloadBytes, linked.Token); }
            catch
            {
                // No transfer to the pump has happened; this helper still owns the buffer.
                _context.Buffers.Return(packet);
                throw;
            }
        }

        // Observe actual emission without a caller token. Canceling a caller's wait
        // cannot cancel the durable ownership/settlement task or refund enqueued bytes.
        var accepted = Session.TryEnqueuePreparedFrame(packet, observeEmission: true,
            CancellationToken.None, failureObserver: null, out var emission, out var rejection);
        var ticket = new Ticket(Credits, publication, packet, expectedWire, accepted, rejection);
        ticket.Completion = accepted
            ? ticket.SettleEnqueuedAsync(emission)
            : ticket.SettleRejectedAsync();
        _tickets.Add(ticket);
        return ticket;
    }

    internal async Task ReadExactFrameAsync(Ticket ticket)
    {
        var read = await _output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            if (!read.Buffer.ToArray().AsSpan().SequenceEqual(ticket.ExpectedWire))
                throw new InvalidOperationException("The real pump must emit the exact prepared StreamData frame.");
        }
        finally { _output.Reader.AdvanceTo(read.Buffer.End); }
    }

    internal sealed class Ticket(GrantAuthority owner, GrantAuthority.Publication publication,
        IRpcByteBufferWriter packet, byte[] expectedWire, bool accepted, Exception? rejection)
    {
        internal readonly bool Accepted = accepted;
        internal readonly Exception? Rejection = rejection;
        internal readonly byte[] ExpectedWire = expectedWire;
        internal readonly IRpcByteBufferWriter Packet = packet;
        internal Task Completion = Task.CompletedTask;
        internal int Settlements;
        internal bool? Refunded;

        internal Task ObserveAsync(CancellationToken token) => Completion.WaitAsync(token);

        internal async Task SettleRejectedAsync()
        {
            await owner.FinishPublicationAsync(publication, accepted: false);
            Refunded = true;
            Interlocked.Increment(ref Settlements);
        }

        internal async Task SettleEnqueuedAsync(ValueTask emission)
        {
            try { await emission.ConfigureAwait(false); }
            finally
            {
                // Even a failed Flush can have emitted bytes. Once accepted by the
                // pump this is not a known-unsent rejection; do not inflate credit.
                await owner.FinishPublicationAsync(publication, accepted: true);
                Refunded = false;
                Interlocked.Increment(ref Settlements);
            }
        }
    }

    internal sealed class PausedFlushWriter(PipeWriter inner) : PipeWriter
    {
        private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Exception? _failure;
        internal Task Entered => _entered.Task;
        internal void Release() => _release.TrySetResult(true);
        internal void FailAndRelease(Exception failure)
        {
            _failure = failure;
            Release();
        }

        public override void Advance(int bytes) => inner.Advance(bytes);
        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
            => FlushPausedAsync(cancellationToken);

        private async ValueTask<FlushResult> FlushPausedAsync(CancellationToken token)
        {
            // Bytes really become readable before the pump sees flush completion.
            // The controllable delay/failure belongs only to this test transport.
            var result = await inner.FlushAsync(token).ConfigureAwait(false);
            _entered.TrySetResult(true);
            await _release.Task.WaitAsync(token).ConfigureAwait(false);
            if (_failure is { } failure) throw failure;
            return result;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Writer.Release();
        await Session.DisposeAsync();
        // Tests explicitly assert expected failures; cleanup still observes every
        // retained completion so faulting tickets cannot become unobserved tasks.
        foreach (var ticket in _tickets)
        {
            try { await ticket.Completion; }
            catch (Exception) { }
        }
        await Credits.DisposeAsync();
        await _output.Reader.CompleteAsync();
        await _input.Writer.CompleteAsync();
        _context.Dispose();
    }
}
