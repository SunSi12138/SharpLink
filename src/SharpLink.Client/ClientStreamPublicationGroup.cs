namespace SharpLink.Client;

/// <summary>
/// The exact physical owner of one outgoing stream group. The original connection-selection
/// reservation protects retirement; its existing pending-capacity charge bounds the physical
/// group and one final FIFO fence until last cleanup. Logical completion refunds neither owner. This owner is deliberately not pooled and never re-resolves a call ID.
/// </summary>
internal sealed partial class ClientStreamPublicationGroup : RpcStreamPublication, IRpcClientStreamSink
{
    private ClientConnection _connection = null!;
    private readonly Lock _gate = new();
    private readonly ProducerSlot[] _tasks;
    private ulong _acceptedLow;
    private ulong _acceptedHigh;
    private int _publishing;
    private bool _sealed;
    private bool _finished;
    private TaskCompletionSource? _publicationJoined;
    private Exception? _registrationFailure;
    private SharpLinkClientStreamModuleLeaseOwner? _producerOwner;
    private PendingRequestTable.ProducerLifetimeLease _pendingLifetime;
    internal bool HasProducerLifetime { get; private set; }
    internal CancellationToken ProducerToken { get; private set; }
    internal void TakeProducerOwner(SharpLinkClientStreamModuleLeaseOwner? owner)
    {
        if (owner is null) return;
        if (_producerOwner is not null || !owner.TryClaimPhysical())
            throw new InvalidOperationException("The exact dynamic producer owner is closed or already claimed.");
        _producerOwner = owner;
    }
    private int _headPublication; // 0=unresolved, 1=definitely neveraccepted, 2=accepted.
    private TaskCompletionSource? _headResolved;
    private TaskCompletionSource? _cleanupCompletion;
    private bool _cleanupSealed;
    private bool _writerInvoked;
    internal int StreamCount => _tasks.Length;
    internal bool HeadPublicationConfirmed => Volatile.Read(ref _headPublication) == 2;

    // ONLY PendingCall.CompletionGate decides the exact transfer outcome. No callbacks here.
    internal void ResolveHeadUnderCompletionGate(bool accepted)
    {
        if (_headPublication == 0) Volatile.Write(ref _headPublication, accepted ? 2 : 1);
    }

    internal void NotifyHeadResolved()
    {
        TaskCompletionSource? signal;
        lock (_gate) signal = _headPublication == 0 ? null : _headResolved;
        signal?.TrySetResult();
    }

    internal void OnPendingTerminal()
    {
        NotifyHeadResolved();
        RequestTerminalCleanup();
    }

    private bool AllTerminalsQueued()
    {
        for (ushort id = 1; id <= _tasks.Length; ++id)
            if (!IsTerminalQueued(id)) return false;
        return true;
    }

    // One bounded exceptional branch, independent of user writers/MoveNext/Dispose. The
    // completion source closes the claim-before-task-publication race without running any
    // timer, transport or callback while holding the group gate.
    private void RequestTerminalCleanup()
    {
        if (Volatile.Read(ref _headPublication) == 1 || AllTerminalsQueued()) return;
        if (Volatile.Read(ref _cleanupCompletion) is not null) return;
        // Sample before the claim so a descheduled publisher cannot reset the first demand's
        // finite budget. Provider failures are themselves owned by this cleanup branch.
        RpcDeadline deadline = default;
        Exception? setupFailure = null;
        try { deadline = RpcDeadline.Create(TimeSpan.FromMilliseconds(Session.BindingAuthority.Limits.OperationDeadlineMs), Session.RuntimeContext.TimeProvider); }
        catch (Exception error) { setupFailure = error; }
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_cleanupSealed || _cleanupCompletion is not null) return;
            completion = _cleanupCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _ = CompleteCleanupAsync(completion, deadline, setupFailure); // Completion owns and observes this entire coordinator.
    }

    private async Task CompleteCleanupAsync(TaskCompletionSource completion, RpcDeadline deadline, Exception? setupFailure)
    {
        // Terminal delivery never runs the cleanup publisher on the receive/cancellation stack.
        // This is one exceptional-path scheduled continuation, not a per-item scheduler.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        Exception? failure = null;
        Task? inner = null;
        Task? close = null;
        var cleanupFailure = new SharpLinkException(SharpLinkErrorCode.Cancelled,
            "Client stream terminated before every declared stream completed.");
        void BeginClose()
        {
            if (close is not null) return;
            // Close initiates synchronously; its Task retains actual pump drain, never a timeout proxy.
            close = Session.CloseAndDrainOutboundAsync(cleanupFailure).AsTask();
            try { _connection.PendingCalls.TryComplete(RequestId, PendingCallCompletionReason.ConnectionClosed, cleanupFailure); }
            finally { NotifyHeadResolved(); }
        }
        try
        {
            if (setupFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(setupFailure).Throw();
            inner = PublishCleanupAsync(cleanupFailure);
            if (!await SharpLinkTimer.WaitForCleanupAsync(inner, deadline, Session.RuntimeContext.TimeProvider, BeginClose).ConfigureAwait(false))
                failure = new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "Client stream terminal cleanup deadline exceeded.");
        }
        catch (Exception error)
        {
            failure = error;
            try { BeginClose(); }
            catch (Exception cleanup) { failure = AddPhysical(failure, cleanup); }
        }
        // Independently join real publication/drain even if watchdog, observer or timer teardown failed.
        if (close is not null)
        {
            try { await close.ConfigureAwait(false); }
            catch (Exception error) { failure = AddPhysical(failure, error); }
        }
        if (inner is not null)
        {
            try { await inner.ConfigureAwait(false); }
            catch (Exception error) { failure = AddPhysical(failure, error); }
        }
        if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
    }

    private async Task PublishCleanupAsync(SharpLinkException error)
    {
        // Return the real inner Task to its watchdog before any transport/deferred callback can
        // block. The coordinator and publisher are distinct bounded exceptional continuations.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        Task head;
        lock (_gate)
            head = _headPublication != 0 ? Task.CompletedTask :
                (_headResolved ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        await head.ConfigureAwait(false);
        if (!HeadPublicationConfirmed) return;
        for (ushort id = 1; id <= _tasks.Length; ++id)
            if (!IsTerminalQueued(id)) Session.SendClientStreamCleanupError(this, id, error);
        // Not BeginFence: producer tasks may still own held MoveNext/send/Dispose work.
        await Session.CompleteStreamCleanupPublicationAsync(this).ConfigureAwait(false);
    }

    // Ownership of the original reservation transfers only after this constructor returns.
    internal ClientStreamPublicationGroup(ClientConnection connection, RpcBindingPublication binding,
        long requestId, int streamCount, long responseDispatcherLease = 0) : base(binding, requestId, checked((ushort)streamCount))
    {
        if (!ReferenceEquals(connection.Session, binding.Session))
            throw new InvalidOperationException("The stream group belongs to another Session.");
        if (streamCount is < 1 or > RpcMethodFacts.MaxClientStreamCount ||
            streamCount > connection.Session.BindingAuthority.Limits.MaxClientStreamsPerCall)
            throw new ArgumentOutOfRangeException(nameof(streamCount), "Declared client streams exceed the exact Session's negotiated limit.");
        _connection = connection;
        _selectionOwned = true;
        _tasks = new ProducerSlot[streamCount];
        _responseDispatcherLease = responseDispatcherLease;
        HasProducerLifetime = connection.PendingCalls.TryAcquireProducerLifetime(requestId, out _pendingLifetime, out var originalToken, this);
        ProducerToken = originalToken;
        if (!HasProducerLifetime) _headPublication = 1;
    }

    public Task SendClientStreamAsync<T>(long requestId, ushort streamId, IAsyncEnumerable<T> stream,
        IRpcCodec<T> codec, CancellationToken cancellationToken = default)
    {
        var index = (int)streamId - 1;
        Exception? rejection = null;
        var terminalizeRejection = false;
        lock (_gate)
        {
            var invalid = !HasProducerLifetime || requestId != RequestId || (uint)index >= (uint)_tasks.Length || _sealed;
            if (!invalid)
            {
                var mask = 1UL << (index & 63);
                invalid = index < 64 ? (_acceptedLow & mask) != 0 : (_acceptedHigh & mask) != 0;
                if (!invalid)
                {
                    if (index < 64) _acceptedLow |= mask; else _acceptedHigh |= mask;
                    ++_publishing; // Register ownership before any user enumerator/codec executes.
                }
            }
            if (invalid)
            {
                var failure = new InvalidOperationException("Stream ID is foreign, duplicate, undeclared, or the physical group is sealed.");
                // At most one invalid-registration failure is retained. Post-seal calls cannot
                // mutate the sealed task set or resurrect the already-returned physical owner.
                if (!_sealed)
                {
                    _registrationFailure ??= failure;
                    terminalizeRejection = true;
                }
                rejection = failure;
            }
        }

        if (rejection is not null)
        {
            // An ignored invalid registration must not leave accepted cooperative siblings
            // waiting forever. No completion callback runs under the group gate.
            if (terminalizeRejection)
            {
                try { _connection.PendingCalls.TryComplete(RequestId, PendingCallCompletionReason.SendFailure, rejection); }
                catch (Exception cleanup) { rejection = AddPhysical(rejection, cleanup); }
            }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(rejection!).Throw();
        }

        Task task;
        try { task = _connection.SendClientStreamCoreAsync(this, streamId, stream, codec, cancellationToken); }
        catch (Exception failure) { task = Task.FromException(failure); }
        TaskCompletionSource? joined = null;
        lock (_gate)
        {
            _tasks[index].Task = task;
            if (--_publishing == 0 && _sealed) joined = _publicationJoined;
        }
        joined?.TrySetResult();
        return task; // The writer and group observe the SAME physical task, not an async adapter.
    }

    internal async Task RunWriterAsync<TStreams>(TStreams streams, CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
    {
        try
        {
            if (await _connection.PendingCalls.WaitForProducerStartAsync(RequestId, this).ConfigureAwait(false))
            {
                _writerInvoked = true;
                await streams.WriteAsync(this, RequestId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            _writerPrimary = error;
            SelectBusinessFailure(error);
        }
        await FinishAsync().ConfigureAwait(false);
    }

    // The sole physical reducer; normal Open/no-error success never waits for a remote result.
    internal async Task FinishAsync(Exception? publicationFailure = null, bool startupFailureOwnedByCaller = false)
    {
        if (!HasProducerLifetime && HasSupervisionAnchor)
        {
            // No adopted Session/ID exists. Setup owns logical delivery; only original local
            // selection cleanup belongs here. Never touch transport/fence on this branch.
            Exception? failure = null;
            bool selection;
            lock (_gate)
            {
                if (_finished) throw new InvalidOperationException("The unadopted stream group was completed twice.");
                _finished = true;
                _sealed = true;
                selection = _selectionOwned;
                _selectionOwned = false;
            }
            try { if (selection) _connection.ReleaseCallAdmissionReservation(); }
            catch (Exception error) { failure = error; }
            MarkNoPending();
            if (failure is not null) throw PhysicalTaskFailureException.From(failure);
            return;
        }
        // A synchronous startup throw has a known direct caller and no opaque writer
        // boundary. That caller retains the exact failure before starting this cleanup;
        // physical callbacks/fences/terminal batches below remain independently owned here.
        if (startupFailureOwnedByCaller && (publicationFailure is null || _writerInvoked))
            throw new InvalidOperationException("Caller-owned startup failure requires an unstarted writer.");
        if (publicationFailure is not null && !startupFailureOwnedByCaller) _writerPrimary = publicationFailure;
        Task publication;
        lock (_gate)
        {
            if (_finished) throw new InvalidOperationException("The physical stream group was completed twice.");
            _finished = true;
            _sealed = true;
            publication = _publishing == 0 ? Task.CompletedTask :
                (_publicationJoined ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        var registered = System.Numerics.BitOperations.PopCount(_acceptedLow) + System.Numerics.BitOperations.PopCount(_acceptedHigh);
        if (_writerInvoked && registered != _tasks.Length)
        {
            _omissionPrimary = new InvalidOperationException("The client stream writer omitted a declared stream.");
            SelectBusinessFailure(_omissionPrimary);
        }
        if (_writerPrimary is not null || _omissionPrimary is not null || !_writerInvoked) RequestTerminalCleanup();
        await publication.ConfigureAwait(false);
        Exception? residual = null;
        // Tasks were already started. This bounded join preserves raw per-slot stage provenance,
        // rather than flattening Task.WhenAll or mistaking cleanup for a delivered business error.
        for (var index = 0; index < _tasks.Length; ++index)
        {
            Task? task;
            lock (_gate) task = _tasks[index].Task;
            Exception? unreported = null;
            if (task is not null)
            {
                try { await task.ConfigureAwait(false); }
                catch (Exception error) { unreported = error; }
            }
            ProducerSlot slot;
            lock (_gate) slot = _tasks[index];
            if (slot.Reported)
            {
                residual = AddPhysical(residual, slot.Independent);
                residual = AddPhysical(residual, await ReduceBusinessAsync(slot.Business).ConfigureAwait(false));
            }
            else if (unreported is not null)
                residual = AddPhysical(residual, AddPhysical(unreported,
                    new InvalidOperationException("A physical producer task failed without reporting its raw stage outcome.")));
            // Every actual task has exited and its raw stages have been observed above. Do not
            // retain completed generic producer state machines until a future logical response.
            lock (_gate) _tasks[index] = default;
        }
        // Exactly one opaque writer candidate may repeat a per-slot independent error. The
        // writer contract carries no propagation receipt, so non-loss takes priority over an
        // unsupported exactly-once diagnostic claim (including identical exception references).
        residual = AddPhysical(residual, await ReduceBusinessAsync(_writerPrimary).ConfigureAwait(false));
        residual = AddPhysical(residual, await ReduceBusinessAsync(_omissionPrimary).ConfigureAwait(false));
        _writerPrimary = null;
        _omissionPrimary = null;
        if (!AllTerminalsQueued()) RequestTerminalCleanup();
        Task cleanupBranch;
        lock (_gate)
        {
            _cleanupSealed = true;
            cleanupBranch = _cleanupCompletion?.Task ?? Task.CompletedTask;
        }
        try { await cleanupBranch.ConfigureAwait(false); }
        catch (Exception error) { residual = AddPhysical(residual, error); }
        try
        {
            if (_headPublication == 2)
                await Session.CompleteStreamPublicationAsync(this).ConfigureAwait(false);
            else if (_headPublication == 0 || _acceptedLow != 0 || _acceptedHigh != 0)
                await Session.CloseAndDrainOutboundAsync(residual ?? new InvalidOperationException(
                    "Stream head publication was uncertain or producers ran without an accepted head.")).ConfigureAwait(false);
        }
        catch (Exception error) { residual = AddPhysical(residual, error); }
        // Atomic against accepted terminal-batch transfer. Later logical-terminal failures stay
        // with their existing caller; this does not extend normal producer lifetime to response.
        residual = ClosePhysicalReduction(residual);
        var producerOwner = _producerOwner;
        _producerOwner = null;
        try { producerOwner?.CompletePhysical(); }
        catch (Exception error) { residual = AddPhysical(residual, error); }
        try { if (_selectionOwned) { _selectionOwned = false; _connection.ReleaseCallAdmissionReservation(); } }
        catch (Exception error) { residual = AddPhysical(residual, error); }
        var pendingLifetime = _pendingLifetime;
        _pendingLifetime = default;
        try { pendingLifetime.Dispose(); }
        catch (Exception error) { residual = AddPhysical(residual, error); }
        if (residual is not null) throw PhysicalTaskFailureException.From(residual);
    }
}
