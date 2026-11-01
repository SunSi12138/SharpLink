using System.Buffers.Binary;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;

namespace SharpLink.UnitTests.Runtime;

// Actual Session/SendPump controls. No alternative queue or ownership algorithm.
public sealed class PreparedFrameEnqueueTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task RefusalDefersTelemetryAndThrowingObserverUntilOutsideCallGate()
    {
        await using var fixture = new PreparedPublicationFixture();
        var gate = new object();
        var observer = new GateObserver(gate, throws: true);
        var scope = new AsyncLocal<bool>();
        var metricUnderGate = false;
        var reservations = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == "SharpLink" && instrument.Name == "sharplink.send.queue.bytes")
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, bytes, _, _) =>
        {
            if (!scope.Value) return;
            metricUnderGate |= Monitor.IsEntered(gate);
            if (bytes > 0) reservations++;
        });
        listener.Start();
        scope.Value = true;
        var admission = fixture.Session.PrepareFrameEnqueue(fixture.Request(11), true,
            CancellationToken.None, observer);
        var reserved = fixture.Session.QueuedSendBytes;
        lock (gate)
        {
            // A logical terminal wins. Deliberately do not TryWrite; cleanup cannot run here.
            Ensure(reserved > 0 && observer.Calls == 0, "only local byte reservation exists before transfer");
        }
        Exception? failure = null;
        try { fixture.Session.FinishPreparedFrameEnqueue(ref admission, out _, out _); }
        catch (Exception error) { failure = error; }
        fixture.Session.FinishPreparedFrameEnqueue(ref admission, out _, out _);
        scope.Value = false;
        Ensure(failure is InvalidOperationException && !admission.Accepted && observer.Calls == 1,
            "one refusal owner survives a throwing observer and repeated finalization");
        Ensure(!observer.UnderGate && !metricUnderGate && reservations == 1 && fixture.Session.QueuedSendBytes == 0,
            "all callbacks run outside arbitration and exact reserved bytes return once");
    }

    [Test]
    public async Task AcceptedTransferRemainsAcceptedAcrossStopBeforeDeferredWake()
    {
        await using var fixture = new PreparedPublicationFixture();
        var gate = new object();
        var observer = new GateObserver(gate, throws: false);
        var admission = fixture.Session.PrepareFrameEnqueue(fixture.Request(12), true,
            CancellationToken.None, observer);
        lock (gate)
            Ensure(fixture.Session.TryTransferPreparedFrame(ref admission) && admission.Accepted,
                "TryWrite commits ownership before deferred wake");
        var reason = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "stop between transfer and wake");
        var drain = fixture.Session.CloseAndDrainOutboundAsync(reason).AsTask();
        fixture.Session.FinishPreparedFrameEnqueue(ref admission, out var emission, out var rejection);
        try { await emission.AsTask().WaitAsync(Timeout); }
        catch (SharpLinkException) { }
        await drain.WaitAsync(Timeout);
        Ensure(admission.Accepted && rejection is null && !observer.UnderGate && fixture.Session.QueuedSendBytes == 0,
            "terminal transport cannot reclassify/refund an accepted frame as caller-owned");
    }

    [Test]
    public async Task SessionCloseAfterReservationRefusesTransferWithoutInlineCleanup()
    {
        await using var fixture = new PreparedPublicationFixture();
        var gate = new object();
        var observer = new GateObserver(gate, throws: false);
        var admission = fixture.Session.PrepareFrameEnqueue(fixture.Request(13), false,
            CancellationToken.None, observer);
        await fixture.Session.CloseAndDrainOutboundAsync(new SharpLinkException(
            SharpLinkErrorCode.ConnectionClosed, "reserved but never transferred")).AsTask().WaitAsync(Timeout);
        lock (gate)
        {
            Ensure(!fixture.Session.TryTransferPreparedFrame(ref admission), "closed Session forbids transfer");
            Ensure(observer.Calls == 0 && fixture.Session.QueuedSendBytes > 0,
                "local reservation remains owned until outside-gate finalization");
        }
        fixture.Session.FinishPreparedFrameEnqueue(ref admission, out _, out var rejection);
        Ensure(rejection is not null && !admission.Accepted && observer.Calls == 1 &&
            !observer.UnderGate && fixture.Session.QueuedSendBytes == 0, "exact refusal cleanup completes once");
    }

    [Test]
    public async Task DrainingAfterPreparationRefusesNewHeadWithoutClosingHealthySession()
    {
        await using var fixture = new PreparedPublicationFixture();
        var admission = fixture.Session.PrepareFrameEnqueue(fixture.Request(14), false,
            CancellationToken.None, failureObserver: null);
        fixture.Session.MarkDraining();
        var gate = new object();
        lock (gate) Ensure(!fixture.Session.TryTransferPreparedFrame(ref admission), "drain won before head transfer");
        fixture.Session.FinishPreparedFrameEnqueue(ref admission, out _, out var rejection);
        Ensure(rejection is SharpLinkException { Code: SharpLinkErrorCode.Unavailable } &&
            fixture.Session.IsConnected && fixture.Session.QueuedSendBytes == 0,
            "phase refusal preserves original draining status and releases only local reservation");
    }

    private sealed class GateObserver(object gate, bool throws) : IRequestEmissionFailureObserver
    {
        internal int Calls;
        internal bool UnderGate;
        public void OnRequestEmissionFailure(long requestId, Exception exception)
        {
            Calls++;
            UnderGate |= Monitor.IsEntered(gate);
            if (throws) throw new InvalidOperationException("injected refusal observer failure");
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class PreparedPublicationFixture : IAsyncDisposable
{
    private readonly Pipe _input = new();
    internal Pipe Output { get; } = new();
    private readonly SharpLinkRuntimeContext _context;
    internal RpcSession Session { get; }
    internal PreparedPublicationFixture()
    {
        _context = new SharpLinkRuntimeContextBuilder().Configure(static options =>
        {
            options.FlowControl.MaxSendQueueBytes = 64 * 1024;
            options.PerformanceProfile = SharpLinkPerformanceProfile.LowLatency;
        }).Build(includeGeneratedAssemblyCatalog: false);
        Session = RpcSessionTestFixture.CreateSessionOverTestTransport(Guid.NewGuid().ToString(),
            _input.Reader, Output.Writer, RpcSessionTestFixture.ClientOptions(_context));
    }
    internal IRpcByteBufferWriter Request(long id)
    {
        var writer = Session.RentFrameWriter();
        using (writer.BeginPacketScope(ProtocolV2FrameType.Request, ProtocolV2FrameFlags.None, (ulong)id))
        {
            var span = writer.GetSpan(16);
            BinaryPrimitives.WriteUInt64LittleEndian(span, 1UL << 16);
            BinaryPrimitives.WriteInt64LittleEndian(span[8..], 1);
            writer.Advance(16);
        }
        return writer;
    }
    public async ValueTask DisposeAsync()
    {
        try { await Session.DisposeAsync(); }
        finally
        {
            try { await _input.Writer.CompleteAsync(); }
            finally
            {
                try { await Output.Reader.CompleteAsync(); }
                finally { _context.Dispose(); }
            }
        }
    }
}
