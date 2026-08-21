namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    public ValueTask DispatchRpcAsync(
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken)
        => DispatchRpcAsync(
            connection,
            requestId,
            flags,
            payload,
            requestCancellationMap,
            serverLoopToken,
            admittedCallState: null,
            admissionGranted: false,
            callCapacityGranted: false,
            allowCompressedCancellationHandoff: true,
            preparedServiceInfo: null,
            reusePreDecodeMetadata: false,
            preDecodeMetadata: null,
            preparedRequest: null);

    // Keeps the existing dispatch-level unit harness stable while the production request-loop
    // entry point uses the six-argument overload above. SharpLinkServer itself is internal.
    private ValueTask DispatchRpcAsync(
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        ServerCallCancellationState? admittedCallState,
        bool admissionGranted)
        => DispatchRpcAsync(
            connection,
            requestId,
            flags,
            payload,
            requestCancellationMap,
            serverLoopToken,
            admittedCallState,
            admissionGranted,
            callCapacityGranted: false,
            allowCompressedCancellationHandoff: false,
            preparedServiceInfo: null,
            reusePreDecodeMetadata: false,
            preDecodeMetadata: null,
            preparedRequest: null);

    private ValueTask HandoffCompressedCancellableRpc(
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        ServerCallCancellationState callState,
        ServiceRegistration serviceInfo,
        RpcMethodDescriptor descriptor,
        bool reusePreDecodeMetadata,
        ServerRequestEnvelope preDecodeRequest)
    {
        IRpcByteBufferWriter? retainedPayload = null;
        try
        {
            ReservePreDecodeRequestStreams(
                connection.Session,
                requestId,
                descriptor.ClientStreamCount,
                callState);
            retainedPayload = CopyAdmissionPayload(payload);
            QueueRetainedCancellableCompressedRpc(
                retainedPayload,
                connection,
                requestId,
                flags,
                requestCancellationMap,
                serverLoopToken,
                callState,
                serviceInfo,
                reusePreDecodeMetadata,
                preDecodeRequest);
            retainedPayload = null;
            return ValueTask.CompletedTask;
        }
        catch (Exception exception)
        {
            if (retainedPayload is not null)
                _runtimeContext.Buffers.Return(retainedPayload);
            CompleteFailedRequestStreams(connection.Session, requestId, exception);
            ReleaseDispatchResources(callState, requestId, requestCancellationMap, connection);
            throw;
        }
    }

    private void QueueRetainedCancellableCompressedRpc(
        IRpcByteBufferWriter retainedPayload,
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        ServerCallCancellationState callState,
        ServiceRegistration serviceInfo,
        bool reusePreDecodeMetadata,
        ServerRequestEnvelope preDecodeRequest)
    {
        var workItem = CompressedCancellableRpcWorkItem.Rent(
            this,
            retainedPayload,
            connection,
            requestId,
            flags,
            requestCancellationMap,
            serverLoopToken,
            callState,
            serviceInfo,
            reusePreDecodeMetadata,
            preDecodeRequest);
        try
        {
            // QueueUserWorkItem flows the current ExecutionContext so request logging scopes,
            // Activity.Current, and other AsyncLocal state remain visible during synchronous
            // decode/service dispatch while the pooled state avoids a per-call closure object.
            if (!ThreadPool.QueueUserWorkItem(
                    static item => item.Execute(),
                    workItem,
                    preferLocal: false))
            {
                throw new InvalidOperationException("Unable to queue compressed RPC dispatch.");
            }
        }
        catch
        {
            workItem.ReturnWithoutExecute();
            throw;
        }
    }

    private void DispatchRetainedCancellableCompressedRpc(
        IRpcByteBufferWriter retainedPayload,
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        ServerCallCancellationState callState,
        ServiceRegistration serviceInfo,
        bool reusePreDecodeMetadata,
        ServerRequestEnvelope preDecodeRequest)
    {
        ValueTask dispatch;
        try
        {
            dispatch = DispatchRpcAsync(
                connection,
                requestId,
                flags,
                new ReadOnlySequence<byte>(retainedPayload.WrittenMemory),
                requestCancellationMap,
                serverLoopToken,
                callState,
                admissionGranted: true,
                callCapacityGranted: true,
                allowCompressedCancellationHandoff: false,
                preparedServiceInfo: serviceInfo,
                reusePreDecodeMetadata: reusePreDecodeMetadata,
                preDecodeMetadata: null,
                preparedRequest: preDecodeRequest);
        }
        catch (Exception exception)
        {
            ObserveUserCall(ValueTask.FromException(exception), requestId);
            return;
        }
        finally
        {
            // DispatchRpcAsync performs compressed request decode synchronously before returning.
            // Once it returns, any async continuation owns only decoded payload/call state and the
            // raw compressed copy can be recycled immediately instead of for the full RPC lifetime.
            _runtimeContext.Buffers.Return(retainedPayload);
        }

        if (!dispatch.IsCompletedSuccessfully)
            ObserveUserCall(dispatch, requestId);
    }

    private void HandoffCompressedCancellableOneWayRpc(
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        ServerCallCancellationState callState,
        ServiceRegistration serviceInfo,
        RpcMethodDescriptor descriptor,
        bool reusePreDecodeMetadata,
        SharpLinkMetadata? preDecodeMetadata)
    {
        IRpcByteBufferWriter? retainedPayload = null;
        try
        {
            ReservePreDecodeRequestStreams(
                connection.Session,
                requestId,
                descriptor.ClientStreamCount,
                callState);
            retainedPayload = CopyAdmissionPayload(payload);
            ObserveUserCall(
                new ValueTask(DispatchRetainedCancellableCompressedOneWayRpcAsync(
                    retainedPayload,
                    connection,
                    requestId,
                    flags,
                    requestCancellationMap,
                    serverLoopToken,
                    descriptor.ClientStreamCount,
                    callState,
                    serviceInfo,
                    reusePreDecodeMetadata,
                    preDecodeMetadata)),
                requestId);
        }
        catch
        {
            if (retainedPayload is not null)
                _runtimeContext.Buffers.Return(retainedPayload);
            DrainFailedOneWayStreams(
                connection.Session,
                requestId,
                descriptor.ClientStreamCount);
            ReleaseOneWayDispatchResources(
                callState,
                requestId,
                requestCancellationMap,
                connection);
            throw;
        }
    }

    private async Task DispatchRetainedCancellableCompressedOneWayRpcAsync(
        IRpcByteBufferWriter retainedPayload,
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        int clientStreamCount,
        ServerCallCancellationState callState,
        ServiceRegistration serviceInfo,
        bool reusePreDecodeMetadata,
        SharpLinkMetadata? preDecodeMetadata)
    {
        await Task.Yield();
        try
        {
            DispatchOneWayRpc(
                connection,
                requestId,
                flags,
                new ReadOnlySequence<byte>(retainedPayload.WrittenMemory),
                requestCancellationMap,
                serverLoopToken,
                callState,
                admissionGranted: true,
                admittedClientStreamCount: clientStreamCount,
                callCapacityGranted: true,
                allowCompressedCancellationHandoff: false,
                preparedServiceInfo: serviceInfo,
                reusePreDecodeMetadata: reusePreDecodeMetadata,
                preDecodeMetadata: preDecodeMetadata);
        }
        finally
        {
            _runtimeContext.Buffers.Return(retainedPayload);
        }
    }

    private sealed class CompressedCancellableRpcWorkItem
    {
        private const int MaxRetained = 4096;
        private static readonly ConcurrentStack<CompressedCancellableRpcWorkItem> Pool = new();
        private static int s_retainedCount;

        private SharpLinkServer? _server;
        private IRpcByteBufferWriter? _retainedPayload;
        private ServerConnectionState? _connection;
        private long _requestId;
        private ProtocolV2FrameFlags _flags;
        private StripedLongMap<ServerCallCancellationState>? _requestCancellationMap;
        private CancellationToken _serverLoopToken;
        private ServerCallCancellationState? _callState;
        private ServiceRegistration? _serviceInfo;
        private bool _reusePreDecodeMetadata;
        private ServerRequestEnvelope _preDecodeRequest;

        private CompressedCancellableRpcWorkItem()
        {
        }

        internal static CompressedCancellableRpcWorkItem Rent(
            SharpLinkServer server,
            IRpcByteBufferWriter retainedPayload,
            ServerConnectionState connection,
            long requestId,
            ProtocolV2FrameFlags flags,
            StripedLongMap<ServerCallCancellationState> requestCancellationMap,
            CancellationToken serverLoopToken,
            ServerCallCancellationState callState,
            ServiceRegistration serviceInfo,
            bool reusePreDecodeMetadata,
            ServerRequestEnvelope preDecodeRequest)
        {
            if (!Pool.TryPop(out var workItem))
                workItem = new CompressedCancellableRpcWorkItem();
            else
                Interlocked.Decrement(ref s_retainedCount);

            workItem._server = server;
            workItem._retainedPayload = retainedPayload;
            workItem._connection = connection;
            workItem._requestId = requestId;
            workItem._flags = flags;
            workItem._requestCancellationMap = requestCancellationMap;
            workItem._serverLoopToken = serverLoopToken;
            workItem._callState = callState;
            workItem._serviceInfo = serviceInfo;
            workItem._reusePreDecodeMetadata = reusePreDecodeMetadata;
            workItem._preDecodeRequest = preDecodeRequest;
            return workItem;
        }

        internal void Execute()
        {
            var server = _server!;
            try
            {
                server.DispatchRetainedCancellableCompressedRpc(
                    _retainedPayload!,
                    _connection!,
                    _requestId,
                    _flags,
                    _requestCancellationMap!,
                    _serverLoopToken,
                    _callState!,
                    _serviceInfo!,
                    _reusePreDecodeMetadata,
                    _preDecodeRequest);
            }
            finally
            {
                Return();
            }
        }

        internal void ReturnWithoutExecute() => Return();

        private void Return()
        {
            _server = null;
            _retainedPayload = null;
            _connection = null;
            _requestId = 0;
            _flags = default;
            _requestCancellationMap = null;
            _serverLoopToken = default;
            _callState = null;
            _serviceInfo = null;
            _reusePreDecodeMetadata = false;
            _preDecodeRequest = default;

            var retained = Interlocked.Increment(ref s_retainedCount);
            if (retained <= MaxRetained)
                Pool.Push(this);
            else
                Interlocked.Decrement(ref s_retainedCount);
        }
    }
}
