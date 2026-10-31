namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private readonly ServerBindingSlotQuota _bindingSlotQuota = new();
    private const int MaximumServerBindingOperations = 4096;
    private int _bindingOperations;

    internal Func<ServerConnectionState, Protocol3BindResult, Task>? BeforeBindingResultForTesting { get; set; }

    // Awaited directly by the single receive loop. It must not dispatch an unbounded
    // detached task per Bind or advance ObserveBindId before this operation completes.
    private async ValueTask ProcessBindContractAsync(ServerConnectionState connection, ulong operationId,
        ReadOnlySequence<byte> payload, CancellationToken cancellationToken)
    {
        var session = connection.Session;
        var options = session.NegotiatedOptions
            ?? throw new SharpLinkProtocolViolationException(ProtocolViolationReason.ProtocolState,
                "BindContract requires a negotiated Session.");
        var limits = options.Authority.Limits;
        var deadline = RpcDeadline.Create(TimeSpan.FromMilliseconds(limits.OperationDeadlineMs), _runtimeContext.TimeProvider);
        var request = Protocol3BindingControlCodec.ReadBindContract(payload);
        Protocol3BindingValidation.ValidateOperationId(operationId);
        if (connection.LifecycleState is not (ServerConnectionLifecycleState.Ready or ServerConnectionLifecycleState.Draining))
            throw new SharpLinkProtocolViolationException(ProtocolViolationReason.ProtocolState,
                "BindContract is not legal in this connection phase.");

        var localReserved = connection.TryReserveBindingOperation(limits.Operations);
        var globalReserved = localReserved && TryReserveServerBindingOperation();
        var frameReserved = globalReserved && connection.TryReserveBindingControlFrame(limits.QueuedControlFrames);
        if (!frameReserved)
        {
            if (globalReserved)
                Interlocked.Decrement(ref _bindingOperations);
            if (localReserved)
                connection.ReleaseBindingOperation();
            // No unaccounted async rejection owner is allocated outside these quotas.
            // Without a reserved final-result owner this exact Session must close.
            var failure = SharpLinkResourceExhaustion.Create(
                globalReserved ? SharpLinkResourceExhaustion.BindingControlQueue : SharpLinkResourceExhaustion.BindingOperations,
                "Binding control ownership capacity is exhausted.");
            connection.FailBindingOperation(failure);
            throw failure;
        }

        ServerBindingDeadline? deadlineOwner = null;
        try
        {
            // Arm the original absolute budget before taking any registry/table lock.
            deadlineOwner = new ServerBindingDeadline(connection, deadline, _runtimeContext.TimeProvider);
            deadlineOwner.Arm();
            cancellationToken.ThrowIfCancellationRequested();
            deadlineOwner.ThrowIfExpired();
            var table = connection.InitializeBindings(_bindingSlotQuota);
            table.ObserveBindId(operationId); // includes unknown contract/hash/receipt failures
            var result = SelectBindingResult(connection, table, operationId, request, options.Authority);
            if (BeforeBindingResultForTesting is { } barrier)
                await barrier(connection, result).ConfigureAwait(false);
            deadlineOwner.ThrowIfExpired();
            await SendBindingResultAsync(session, operationId, result, limits.Slots, cancellationToken).ConfigureAwait(false);
            // Conservative cold-path FIFO fence: retains the bounded control-frame owner
            // until transport flush, not merely enqueue. No throughput acceptance is claimed.
            await session.FlushSendQueueAsync(cancellationToken).ConfigureAwait(false);
            deadlineOwner.Complete();
            table.CompleteBindId(operationId);
        }
        catch (Exception exception)
        {
            // Cancellation/failed enqueue may race visibility. Never pretend it retracts a
            // successful binding: terminal-close reclaims this Session's exact table instead.
            connection.FailBindingOperation(exception);
            throw;
        }
        finally
        {
            try
            {
                if (deadlineOwner is not null)
                    await deadlineOwner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                connection.ReleaseBindingControlFrame();
                connection.ReleaseBindingOperation();
                Interlocked.Decrement(ref _bindingOperations);
            }
        }
    }

    private Protocol3BindResult SelectBindingResult(ServerConnectionState connection, ServerBindingTable table,
        ulong operationId, Protocol3BindRequest request, Protocol3BindingAuthority authority)
    {
        if (authority.ServerInstanceId != _desiredSessionServerInstanceId || authority.RegistryEpoch != _bindingRegistryEpoch)
            throw new SharpLinkProtocolViolationException(ProtocolViolationReason.InternalState,
                "The physical Session binding authority does not belong to this server lifetime.");
        if (CurrentState != ServerState.Running || connection.LifecycleState != ServerConnectionLifecycleState.Ready)
            return Protocol3BindResult.Failure(Protocol3BindStatus.SessionDraining);
        if (connection.AuthenticationContext?.IsExpired(_runtimeContext.TimeProvider.GetUtcNow()) == true)
            return Protocol3BindResult.Failure(Protocol3BindStatus.ScopeDenied);
        lock (_registryGate)
        {
            if (CurrentState != ServerState.Running || connection.LifecycleState != ServerConnectionLifecycleState.Ready ||
                !connection.Session.CanAcceptCalls || table.IsClosed)
                return Protocol3BindResult.Failure(Protocol3BindStatus.SessionDraining);
            if (request.Receipt is { } receipt &&
                (receipt.ServerInstanceId != authority.ServerInstanceId || receipt.RegistryEpoch != authority.RegistryEpoch))
                return Protocol3BindResult.Failure(Protocol3BindStatus.ReceiptScopeMismatch);
            if (!_services.TryGetValue(request.ContractId, out var service))
                return Protocol3BindResult.Failure(Protocol3BindStatus.UnknownContract);
            var registration = service.Binding
                ?? throw new SharpLinkProtocolViolationException(ProtocolViolationReason.InternalState,
                    "Published service has no stable binding registration.");
            if (registration.ContractId != request.ContractId)
                throw new SharpLinkProtocolViolationException(ProtocolViolationReason.InternalState,
                    "Published binding registration has an inconsistent contract identity.");
            if (registration.Hash != request.Hash)
                return Protocol3BindResult.Failure(Protocol3BindStatus.HashMismatch);
            if (registration.IsRetired)
                return Protocol3BindResult.Failure(Protocol3BindStatus.RegistrationRetiring);
            if (request.Receipt is { } requirement)
            {
                if (request.Requirement != Protocol3BindingRequirement.Compatible &&
                    (ulong)registration.PublicationGeneration < requirement.Generation)
                    return Protocol3BindResult.Failure(Protocol3BindStatus.BelowMinimum);
                if (request.Requirement == Protocol3BindingRequirement.ExactRegistration &&
                    (ulong)registration.RegistrationId != requirement.RegistrationId)
                    return Protocol3BindResult.Failure(Protocol3BindStatus.RegistrationMismatch);
            }
            var status = table.TryBind(operationId, registration, out var entry);
            if (status != Protocol3BindStatus.Success)
                return Protocol3BindResult.Failure(status);
            return new Protocol3BindResult(Protocol3BindStatus.Success, entry!.Token, registration.ContractId,
                registration.Hash, (ulong)registration.RegistrationId, (ulong)registration.PublicationGeneration,
                authority.ServerInstanceId, authority.RegistryEpoch);
        }
    }

    private static async ValueTask SendBindingResultAsync(RpcSession session, ulong operationId,
        Protocol3BindResult result, uint slots, CancellationToken cancellationToken)
    {
        var writer = session.RentFrameWriter();
        var ownsWriter = true;
        try
        {
            using (writer.BeginPacketScope(ProtocolV2FrameType.BindResult, ProtocolV2FrameFlags.None, operationId))
                Protocol3BindingControlCodec.WriteBindResult(writer, result, slots);
            ownsWriter = false;
            await session.SendPacketWithBackpressureAsync(writer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (ownsWriter)
                session.RuntimeContext.Buffers.Return(writer);
        }
    }

    private bool TryReserveServerBindingOperation()
    {
        while (true)
        {
            var current = Volatile.Read(ref _bindingOperations);
            if (current >= MaximumServerBindingOperations)
                return false;
            if (Interlocked.CompareExchange(ref _bindingOperations, current + 1, current) == current)
                return true;
        }
    }

    private sealed class ServerBindingDeadline : IAsyncDisposable
    {
        private readonly ServerConnectionState _connection;
        private readonly RpcDeadline _deadline;
        private readonly TimeProvider _timeProvider;
        private readonly ITimer _timer;
        private int _completed;

        internal ServerBindingDeadline(ServerConnectionState connection, RpcDeadline deadline, TimeProvider timeProvider)
        {
            _connection = connection;
            _deadline = deadline;
            _timeProvider = timeProvider;
            _timer = timeProvider.CreateTimer(static state => ((ServerBindingDeadline)state!).OnTimer(), this,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        // Owner is installed in the caller's finally BEFORE fallible arming. In
        // particular, a throwing Change never bypasses async timer cleanup/quota return.
        internal void Arm()
        {
            var remaining = _deadline.GetRemaining(_timeProvider);
            if (remaining == TimeSpan.Zero)
                Expire();
            else if (!_timer.Change(remaining, Timeout.InfiniteTimeSpan))
                throw new InvalidOperationException("Binding deadline timer could not be armed.");
        }

        internal void ThrowIfExpired()
        {
            if (_deadline.IsExpired(_timeProvider))
                Expire();
            if (Volatile.Read(ref _completed) == 2)
                throw ExpiredException();
        }

        internal void Complete()
        {
            ThrowIfExpired();
            if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
                throw ExpiredException();
        }

        private void OnTimer()
        {
            try
            {
                if (Volatile.Read(ref _completed) != 0)
                    return;
                var remaining = _deadline.GetRemaining(_timeProvider);
                if (remaining > TimeSpan.Zero)
                {
                    // An early provider callback retains the SAME absolute deadline.
                    if (!_timer.Change(remaining, Timeout.InfiniteTimeSpan) && Volatile.Read(ref _completed) == 0)
                        throw new InvalidOperationException("Binding deadline timer could not retain its original deadline.");
                    return;
                }
                Expire();
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _completed) != 0)
            {
                // Completion won while an early callback was about to re-arm its timer.
            }
            catch (Exception exception)
            {
                if (Interlocked.CompareExchange(ref _completed, 2, 0) == 0)
                    _connection.FailBindingOperation(exception);
            }
        }

        private void Expire()
        {
            if (Interlocked.CompareExchange(ref _completed, 2, 0) == 0)
                _connection.FailBindingOperation(ExpiredException());
        }

        private static SharpLinkException ExpiredException()
            => new(SharpLinkErrorCode.DeadlineExceeded, "Binding control operation exceeded its original deadline.");

        public async ValueTask DisposeAsync()
        {
            Interlocked.CompareExchange(ref _completed, 1, 0);
            await _timer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
