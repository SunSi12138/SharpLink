using System.Runtime.ExceptionServices;

namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private void StartServerStreamingInvoker<TRequest, TResponse>(
        PooledAsyncStreamDispatcher<TResponse> dispatcher,
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
    {
        var bootstrap = new ServerStreamBootstrap<TRequest, TResponse>(
            this, dispatcher, method, request, requestCodec, control, cancellationToken);
        // Start gives the same initial ExecutionContext/SynchronizationContext isolation as
        // the previous async method. No Task is accessed and no state machine is boxed here.
        var builder = AsyncTaskMethodBuilder.Create();
        builder.Start(ref bootstrap);
        bootstrap.Failure?.Throw();
        if (bootstrap.Pending is { } operation)
        {
            try
            {
                _frameworkTasks.RegisterOperation(operation);
            }
            finally
            {
                // Rejection must not abandon the already-started physical send.
                operation.ObserveCompletion();
            }
        }
        else
        {
            TrackFrameworkTask(bootstrap.Completion!, "ServerStreamingInvoker");
        }
    }

    private struct ServerStreamBootstrap<TRequest, TResponse>(
        SharpLinkClient client,
        PooledAsyncStreamDispatcher<TResponse> dispatcher,
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        ResolvedCallControl control,
        CancellationToken cancellationToken) : IAsyncStateMachine
    {
        internal ExceptionDispatchInfo? Failure;
        internal Task? Completion;
        internal FrameworkValueTaskOperation? Pending;

        public void MoveNext()
        {
            try
            {
                Completion = client.BootstrapServerStream(
                    dispatcher, method, request, requestCodec, control, cancellationToken, out Pending);
            }
            catch (Exception exception)
            {
                Failure = ExceptionDispatchInfo.Capture(exception);
            }
        }

        public void SetStateMachine(IAsyncStateMachine stateMachine)
            => throw new InvalidOperationException("The synchronous bootstrap cannot suspend.");
    }

    private Task? BootstrapServerStream<TRequest, TResponse>(
        PooledAsyncStreamDispatcher<TResponse> dispatcher,
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        ResolvedCallControl control,
        CancellationToken cancellationToken,
        out FrameworkValueTaskOperation? pending)
    {
        pending = null;
        long registrationLease;
        try
        {
            registrationLease = dispatcher.RetainForRegistration();
        }
        catch (Exception exception)
        {
            return CreateBootstrapFailureTask(exception);
        }

        StreamCallRegistration registration = default;
        ValueTask send;
        try
        {
            // This helper has no asynchronous branch: it returns a completed value or throws.
            registration = PrepareGeneratedServerStreamAsync(
                dispatcher, PendingCallKind.ServerStreaming, method, control, cancellationToken,
                captureTerminalSignal: control.Deadline.HasValue).GetAwaiter().GetResult();
            try
            {
                send = SendRpcCall(
                    registration.Connection.Session,
                    method.ContractId,
                    method.MethodId,
                    registration.RequestId,
                    cancellationToken.CanBeCanceled || control.Deadline.HasValue
                        ? ProtocolV2FrameFlags.Cancellable
                        : ProtocolV2FrameFlags.None,
                    request,
                    requestCodec,
                    control.Deadline,
                    control.Metadata,
                    observeEmission: control.Deadline.HasValue,
                    cancellationToken: registration.TerminalSignal?.Token ?? CancellationToken.None,
                    publicationTable: registration.Connection.PendingCalls);
            }
            catch
            {
                registration.TerminalSignal?.StopObservingTerminal();
                throw;
            }
        }
        catch (Exception exception)
        {
            Task completion = Task.CompletedTask;
            try
            {
                try
                {
                    CompleteFailedGeneratedStream(dispatcher, registration.Connection, registration.RequestId, exception);
                }
                finally
                {
                    dispatcher.ReleaseRegistrationRetention(registrationLease);
                }
            }
            catch (Exception cleanupFailure)
            {
                completion = CreateBootstrapFailureTask(cleanupFailure);
            }
            return completion;
        }

        var awaiter = send.ConfigureAwait(false).GetAwaiter();
        if (awaiter.IsCompleted)
        {
            Task completion = Task.CompletedTask;
            try
            {
                CompleteServerStreamBootstrap(dispatcher, registrationLease, registration, awaiter);
            }
            catch (Exception exception)
            {
                completion = CreateBootstrapFailureTask(exception);
            }
            return completion;
        }

        pending = new ServerStreamBootstrapCompletion<TResponse>(
            this, dispatcher, registrationLease, registration, awaiter);
        return null;
    }

    private static Task CreateBootstrapFailureTask(Exception exception)
    {
        if (exception is not OperationCanceledException canceled)
            return Task.FromException(exception);
        // AsyncTaskMethodBuilder preserves even an uncanceled OCE token.
        var completion = new TaskCompletionSource();
        completion.SetCanceled(canceled.CancellationToken);
        return completion.Task;
    }

    private void CompleteServerStreamBootstrap<TResponse>(
        PooledAsyncStreamDispatcher<TResponse> dispatcher,
        long registrationLease,
        StreamCallRegistration registration,
        ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter awaiter)
    {
        try
        {
            try
            {
                awaiter.GetResult();
            }
            finally
            {
                registration.TerminalSignal?.StopObservingTerminal();
            }
        }
        catch (Exception exception)
        {
            CompleteFailedGeneratedStream(dispatcher, registration.Connection, registration.RequestId, exception);
        }
        finally
        {
            dispatcher.ReleaseRegistrationRetention(registrationLease);
        }
    }

    private sealed class ServerStreamBootstrapCompletion<TResponse> : FrameworkValueTaskOperation
    {
        private SharpLinkClient? _client;
        private PooledAsyncStreamDispatcher<TResponse>? _dispatcher;
        private readonly long _registrationLease;
        private StreamCallRegistration _registration;

        internal ServerStreamBootstrapCompletion(
            SharpLinkClient client,
            PooledAsyncStreamDispatcher<TResponse> dispatcher,
            long registrationLease,
            StreamCallRegistration registration,
            ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter awaiter)
            : base(client._frameworkTasks, "ServerStreamingInvoker", IsExpectedStopException, awaiter)
        {
            _client = client;
            _dispatcher = dispatcher;
            _registrationLease = registrationLease;
            _registration = registration;
        }

        protected override void CompletePhysicalOperation(ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter awaiter)
        {
            try
            {
                _client!.CompleteServerStreamBootstrap(_dispatcher!, _registrationLease, _registration, awaiter);
            }
            finally
            {
                _client = null;
                _dispatcher = null;
                _registration = default;
            }
        }
    }
}
