using System.Buffers.Binary;
using System.Reflection;
using System.Linq;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

// Component fixture around the production group, not a generated-invocation/supervision model.
// Caller supplies either a genuinely prepared exact-session BoundTarget, or the explicitly
// host-only TestRpcBindingPublication used by direct PipeSession ownership/credit controls.
internal sealed class DirectClientStreamTestGroup : IAsyncDisposable
{
    private readonly ClientConnection _connection;
    private Task? _core;
    private Task? _finish;
    private bool _disposed;
    internal ClientStreamPublicationGroup Group { get; }
    internal Task<int> Response { get; }
    internal long RequestId => Group.RequestId;

    internal DirectClientStreamTestGroup(ClientConnection connection, RpcBindingPublication binding,
        IRpcCodec<int> responseCodec, RpcDeadline deadline = default, CancellationToken cancellationToken = default)
    {
        _connection = connection;
        if (!ReferenceEquals(connection.Session, binding.Session))
            throw new InvalidOperationException("The binding must belong to this exact physical test connection.");
        if (!connection.TryReserveExactCallAdmission())
            throw new InvalidOperationException("The fixture must own an actual connection selection reservation.");
        long requestId = 0;
        Task<int>? response = null;
        try
        {
            var operation = connection.PendingCalls.Rent(responseCodec, PendingCallKind.ClientStreaming,
                deadline, cancellationToken, out requestId, hasResponsePayload: true, responseNullable: false);
            Response = response = operation.AsValueTask().AsTask();
            Group = new ClientStreamPublicationGroup(connection, binding, requestId, streamCount: 1);
            // The constructor now owns the original selection reservation and pending producer lease.
        }
        catch (Exception error)
        {
            Exception? failure = error;
            try
            {
                if (requestId != 0)
                    connection.PendingCalls.TryComplete(requestId, PendingCallCompletionReason.SendFailure, error);
            }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
            try { connection.ReleaseCallAdmissionReservation(); }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
            // A failed constructor cannot hand response ownership to DisposeAsync. Observe its
            // eventual fault without blocking construction or replacing the original failure.
            if (response is not null)
                _ = response.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure!).Throw();
            throw;
        }
    }

    internal static RpcBindingPublication CapturePreparedBinding(IRpcContractChannel channel, ClientConnection connection)
    {
        var site = channel as SharpLinkClient.ContractCallSite
            ?? throw new InvalidOperationException("The manual invocation fixture must expose its actual static site.");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var gate = (Lock)typeof(SharpLinkClient.ContractCallSite).GetField("_bindingGate", flags)!.GetValue(site)!;
        var targetsField = typeof(SharpLinkClient.ContractCallSite).GetField("_boundTargets", flags)!;
        lock (gate)
        {
            var targets = (SharpLinkClient.ContractCallSite.BoundTarget[])targetsField.GetValue(site)!;
            return targets.Single(target => ReferenceEquals(target.Connection, connection) && target.IsEligible);
        }
    }

    internal async Task PublishHeadAsync(long methodId = 1)
    {
        var session = _connection.Session;
        IRpcByteBufferWriter? ownedWriter = session.RentFrameWriter();
        try
        {
            using (ownedWriter.BeginPacketScope(ProtocolV2FrameType.Request, ProtocolV2FrameFlags.None, (ulong)RequestId))
            {
                var payload = ownedWriter.GetSpan(16);
                BinaryPrimitives.WriteUInt64LittleEndian(payload, Group.Binding.Token);
                BinaryPrimitives.WriteInt64LittleEndian(payload[8..], methodId);
                ownedWriter.Advance(16);
            }
            var packet = ownedWriter;
            ownedWriter = null; // PrepareOutboundFrame takes the packet even when it throws.
            ownedWriter = session.PrepareOutboundFrame(packet, Group.ProducerToken);
            packet = ownedWriter;
            ownedWriter = null; // The pending publication gate takes prepared ownership on every outcome.
            if (!_connection.PendingCalls.TryPublishRequest(RequestId, session, packet, false, Group.ProducerToken,
                    _connection.PendingCalls, out _, out var rejection, Group))
                throw rejection ?? new InvalidOperationException("The actual pending gate rejected the fixture stream head.");
            _connection.PendingCalls.ReceiveRequestAccepted((ulong)RequestId);
            await session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception error)
        {
            Exception? failure = error;
            try { if (ownedWriter is not null) session.RuntimeContext.Buffers.Return(ownedWriter); }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
            try { _connection.PendingCalls.TryComplete(RequestId, PendingCallCompletionReason.SendFailure, error); }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure!).Throw();
            throw;
        }
    }

    internal Task SendAsync<T>(IAsyncEnumerable<T> stream, IRpcCodec<T> codec)
    {
        if (_core is not null)
            throw new InvalidOperationException("The fixture declares exactly one client stream.");
        // Group.Send returns the SAME SendClientStreamCoreAsync Task and records it before return.
        _core = Group.SendClientStreamAsync(RequestId, 1, stream, codec, Group.ProducerToken);
        return _core;
    }

    internal Task FinishAsync() => _finish ??= Group.FinishAsync();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Exception? failure = null;
        // Resolve the real pending head disposition even if head construction/publication
        // failed before any core started. This also releases cooperative credit waits; user-held
        // MoveNext/Dispose barriers remain caller-owned and are released by each test's finally.
        // Normal EOF has no response in these component controls, so teardown cancels that
        // remaining logical operation only after the test has observed the original core.
        try { _connection.PendingCalls.TryComplete(RequestId, PendingCallCompletionReason.UserCancellation); }
        catch (Exception cleanup) { failure = cleanup; }
        try { await FinishAsync().ConfigureAwait(false); }
        catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
        // Logical outcomes are asserted by each test; physical independent errors remain in Finish.
        try { await Response.ConfigureAwait(false); }
        catch (Exception) { }
        if (failure is not null) throw failure;
    }
}
