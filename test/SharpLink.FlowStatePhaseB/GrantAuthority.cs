using System.Threading.Channels;

namespace SharpLink.FlowStatePhaseB;

// Isolated SEND-SIDE model, not a replacement for StreamFlowController.
// One unsettled item receipt / one pending acquire per stream. Receive batching
// and wire integration remain outside this research model.
internal sealed partial class GrantAuthority : IAsyncDisposable
{
    private readonly int _streamWindow;
    private readonly int _connectionWindow;
    private readonly int _grantBytes;
    private readonly int _maxStreams;
    private readonly int _maxItemBytes;
    private readonly Channel<IOwnerCommand> _commands;
    private readonly IOwnerCommand _wake;
    private readonly Task _owner;
    private readonly Dictionary<long, State> _states = [];
    private readonly Stack<State> _pool = [];
    private readonly LinkedList<AcquireCommand> _waiters = [];
    private long _generation;
    private long _free;
    private int _pressure;
    private bool _terminal;
    internal long QueueSubmissions;
    internal long AcquireSubmissions;
    internal long Revocations;
    internal long AdmittedWaiters;
    internal long ReusableCommandAllocations;
    internal long QueueBackpressureWaits;

    internal GrantAuthority(int streamWindow, int connectionWindow, int grantBytes,
        int maxStreams = 128, int maxItemBytes = 4 * 1024 * 1024, int? queueCapacity = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(streamWindow);
        ArgumentOutOfRangeException.ThrowIfLessThan(connectionWindow, streamWindow);
        ArgumentOutOfRangeException.ThrowIfNegative(grantBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxStreams);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxItemBytes);
        _streamWindow = streamWindow;
        _connectionWindow = connectionWindow;
        _grantBytes = grantBytes;
        _maxStreams = maxStreams;
        _maxItemBytes = maxItemBytes;
        _free = connectionWindow;
        _wake = new ActionCommand(static () => { });
        _commands = Channel.CreateBounded<IOwnerCommand>(new BoundedChannelOptions(queueCapacity ?? checked(maxStreams * 4))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _owner = Task.Run(RunOwnerAsync);
    }

    internal readonly record struct Lease(State State, long Generation);
    internal readonly record struct Receipt(Lease Lease, long Sequence, int Bytes);
    internal readonly record struct Ledger(long Free, long Unspent, long Pending,
        long Outstanding, int Waiters, int Retained, int Publications);

    internal sealed class State
    {
        internal readonly GrantAuthority Owner;
        internal AcquireCommand AcquireCommand;
        internal UpdateCommand UpdateCommand;

        internal State(GrantAuthority owner)
        {
            Owner = owner;
            AcquireCommand = new AcquireCommand(owner);
            UpdateCommand = new UpdateCommand(owner);
        }
        internal readonly Lock Gate = new();
        internal long Key;
        internal long Generation;
        internal bool Attached;
        internal bool Closed;
        internal bool AcquirePending;
        internal long Credit;
        internal long Grant;
        internal long Outstanding;
        internal long Sequence;
        internal int Pending;
        internal bool PublicationActive;
        internal int PublicationUncredited;
        internal int PublicationBytes;
    }

    private sealed class ActionCommand(Action action) : IOwnerCommand
    {
        public void Execute() => action();
        public void Fail(Exception error) => throw error;
    }

    private async Task RunOwnerAsync()
    {
        await foreach (var command in _commands.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            command.Execute();
            DrainWaiters();
        }
    }

    private async ValueTask SubmitAsync(Action action)
    {
        // Count before publication: a consumer may finish before WriteAsync returns.
        Interlocked.Increment(ref QueueSubmissions);
        try { await _commands.Writer.WriteAsync(new ActionCommand(action)).ConfigureAwait(false); }
        catch { Interlocked.Decrement(ref QueueSubmissions); throw; }
    }

    private ValueTask<T> SubmitReusable<T>(ReusableOwnerCommand<T> command, ValueTask<T> result)
    {
        Interlocked.Increment(ref QueueSubmissions);
        if (_commands.Writer.TryWrite(command)) return result;
        Interlocked.Increment(ref QueueBackpressureWaits);
        return WaitForQueueAsync(command, result);
    }

    private async ValueTask<T> WaitForQueueAsync<T>(ReusableOwnerCommand<T> command, ValueTask<T> result)
    {
        try { await _commands.Writer.WriteAsync(command).ConfigureAwait(false); }
        catch (Exception error)
        {
            Interlocked.Decrement(ref QueueSubmissions);
            command.Fail(error);
        }
        // Consume the same captured token even on enqueue failure, releasing its slot.
        return await result.ConfigureAwait(false);
    }

    private void WakeCanceledWaiters()
    {
        // Cancellation never captures a reusable request/generation. A full queue
        // already guarantees an owner turn and a scan, so a wake can be coalesced.
        Interlocked.Increment(ref QueueSubmissions);
        if (!_commands.Writer.TryWrite(_wake)) Interlocked.Decrement(ref QueueSubmissions);
    }

    internal async Task<T> OnOwnerAsync<T>(Func<T> operation)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        await SubmitAsync(() =>
        {
            try { completion.SetResult(operation()); }
            catch (Exception error) { completion.SetException(error); }
        }).ConfigureAwait(false);
        return await completion.Task.ConfigureAwait(false);
    }

    internal Task<Lease> OpenAsync(long key) => OnOwnerAsync(() =>
    {
        if (_terminal) throw new InvalidOperationException("Connection is terminal.");
        if (_states.ContainsKey(key)) throw new InvalidOperationException("Active key or tombstone exists.");
        if (_states.Count == _maxStreams) throw new InvalidOperationException("Stream limit reached.");
        var state = _pool.TryPop(out var pooled) ? pooled : new State(this);
        lock (state.Gate)
        {
            // A closed stream can be rented before an old caller consumes completion.
            // Replace only the held slot; never reset a previous generation's token.
            if (state.AcquireCommand.IsBusy) state.AcquireCommand = new AcquireCommand(this);
            if (state.UpdateCommand.IsBusy) state.UpdateCommand = new UpdateCommand(this);
            state.Key = key;
            state.Generation = checked(++_generation);
            state.Attached = true;
            state.Closed = false;
            state.AcquirePending = false;
            state.Credit = _streamWindow;
            state.Grant = state.Outstanding = state.Sequence = 0;
            state.Pending = 0;
            state.PublicationActive = false;
            state.PublicationUncredited = state.PublicationBytes = 0;
            _states.Add(key, state);
            return new Lease(state, state.Generation);
        }
    });

    private State Validate(Lease lease)
    {
        var state = lease.State;
        if (state is null || state.Owner != this || !state.Attached || state.Generation != lease.Generation)
            throw new InvalidOperationException("Stale or foreign lease.");
        return state;
    }

    internal ValueTask<Receipt> AcquireAsync(Lease lease, int bytes, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, _maxItemBytes);
        var state = lease.State ?? throw new InvalidOperationException("Unresolved lease.");
        lock (state.Gate)
        {
            Validate(lease);
            if (state.Closed) throw new InvalidOperationException("Closed stream.");
            if (state.Pending != 0 || state.AcquirePending || state.PublicationActive)
                throw new InvalidOperationException("Settle the previous receipt/acquire first.");
            if (Volatile.Read(ref _pressure) == 0 && state.Grant >= bytes && state.Credit >= bytes)
            {
                state.Grant -= bytes;
                state.Credit -= bytes;
                state.Pending = bytes;
                return ValueTask.FromResult(new Receipt(lease, ++state.Sequence, bytes));
            }
            return AcquireOnOwnerLocked(lease, bytes, token);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private ValueTask<Receipt> AcquireOnOwnerLocked(Lease lease, int bytes, CancellationToken token)
    {
        // Keep command preparation off the local-grant fast path. The caller still
        // owns the stream gate until the slot is bound and submission is initiated.
        var command = lease.State.AcquireCommand;
        var result = command.Prepare(lease, bytes, token);
        lease.State.AcquirePending = true;
        Interlocked.Increment(ref AcquireSubmissions);
        return SubmitReusable(command, result);
    }

    private bool HasCredit(long credit, int bytes, int window)
        => credit >= bytes || bytes > window && credit == window;

    private Receipt Reserve(AcquireCommand request, bool extras)
    {
        var state = request.Lease.State;
        _free -= request.Bytes;
        state.Credit -= request.Bytes;
        state.Pending = request.Bytes;
        if (extras && _grantBytes > request.Bytes)
        {
            var extra = Math.Max(0, Math.Min(_grantBytes - request.Bytes, Math.Min(_free, state.Credit)));
            _free -= extra;
            state.Grant += extra;
        }
        return new Receipt(request.Lease, ++state.Sequence, request.Bytes);
    }

    private void AdmitOrQueue(AcquireCommand request)
    {
        try
        {
            lock (request.Lease.State.Gate)
            {
                var state = Validate(request.Lease);
                request.Token.ThrowIfCancellationRequested();
                if (_terminal || state.Closed) throw new InvalidOperationException("Terminal stream.");
                _free += state.Grant;
                state.Grant = 0;
                if (_waiters.Count == 0 && HasCredit(_free, request.Bytes, _connectionWindow) &&
                    HasCredit(state.Credit, request.Bytes, _streamWindow))
                {
                    request.Complete(Reserve(request, extras: true));
                    return;
                }
            }
            // Freeze new local admissions before reclaiming any grant. Taking every
            // stream gate drains in-progress local debit/settlement operations.
            Volatile.Write(ref _pressure, 1);
            _waiters.AddLast(request.Node);
            RevokeUnspent();
        }
        catch (OperationCanceledException) { request.Fail(new OperationCanceledException(request.Token)); }
        catch (Exception error) { request.Fail(error); }
    }

    private void RevokeUnspent()
    {
        foreach (var state in _states.Values)
        {
            lock (state.Gate)
            {
                if (state.Grant != 0) Revocations++;
                _free += state.Grant;
                state.Grant = 0;
            }
        }
    }

    private void DrainWaiters()
    {
        // Cancellation is independent of the credit-blocked FIFO prefix.
        var canceled = _waiters.First;
        while (canceled is not null)
        {
            var next = canceled.Next;
            var request = canceled.Value;
            lock (request.Lease.State.Gate)
            {
                var state = request.Lease.State;
                if (request.Token.IsCancellationRequested)
                {
                    _waiters.Remove(canceled);
                    request.Fail(new OperationCanceledException(request.Token));
                }
                else if (_terminal || state.Closed || !state.Attached || state.Generation != request.Lease.Generation)
                {
                    _waiters.Remove(canceled);
                    request.Fail(new InvalidOperationException("Terminal or stale waiter."));
                }
            }
            canceled = next;
        }
        var node = _waiters.First;
        while (node is not null)
        {
            var next = node.Next;
            var request = node.Value;
            Receipt receipt = default;
            Exception? error = null;
            bool removed = true;
            lock (request.Lease.State.Gate)
            {
                try
                {
                    var state = Validate(request.Lease);
                    request.Token.ThrowIfCancellationRequested();
                    if (_terminal || state.Closed) throw new InvalidOperationException("Terminal stream.");
                    if (!HasCredit(_free, request.Bytes, _connectionWindow)) break;
                    if (!HasCredit(state.Credit, request.Bytes, _streamWindow)) removed = false;
                    else
                    {
                        // Required bytes only while waiters exist; never speculate a
                        // chunk ahead of an older connection-credit-blocked request.
                        receipt = Reserve(request, extras: false);
                        AdmittedWaiters++;
                    }
                }
                catch (Exception failure) { error = failure; }
            }
            if (removed)
            {
                _waiters.Remove(node);
                if (error is null) request.Complete(receipt);
                else request.Fail(error);
            }
            node = next;
        }
        if (_waiters.Count == 0) Volatile.Write(ref _pressure, 0);
    }

    internal void Commit(Receipt receipt)
    {
        lock (receipt.Lease.State.Gate)
        {
            var state = Validate(receipt.Lease);
            if (state.Closed || state.Pending != receipt.Bytes || state.Sequence != receipt.Sequence)
                throw new InvalidOperationException("Receipt already settled or revoked.");
            state.Outstanding += state.Pending;
            state.Pending = 0;
        }
    }

    internal async ValueTask ReturnUnsentAsync(Receipt receipt)
    {
        var state = receipt.Lease.State;
        lock (state.Gate)
        {
            if (state.Owner != this) throw new InvalidOperationException("Foreign receipt.");
            if (!state.Attached || state.Generation != receipt.Lease.Generation || state.Closed) return;
            if (state.Pending != receipt.Bytes || state.Sequence != receipt.Sequence)
                throw new InvalidOperationException("Unsent receipt already settled.");
            state.Credit += state.Pending;
            state.Grant += state.Pending;
            state.Pending = 0;
            if (Volatile.Read(ref _pressure) == 0) return;
        }
        // Under waiter pressure return local surplus to the owner promptly.
        await OnOwnerAsync(() => { RevokeUnspent(); return true; }).ConfigureAwait(false);
    }

    internal ValueTask<bool> WindowUpdateAsync(Lease lease, int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        var state = lease.State ?? throw new InvalidOperationException("Unresolved lease.");
        UpdateCommand command;
        ValueTask<bool> result;
        lock (state.Gate)
        {
            if (state.Owner != this) throw new InvalidOperationException("Foreign lease.");
            // Preserve overlapping update support; held results are never overwritten.
            command = state.UpdateCommand.IsBusy ? new UpdateCommand(this) : state.UpdateCommand;
            result = command.Prepare(lease, bytes);
        }
        return SubmitReusable(command, result);
    }

    private bool ApplyWindowUpdate(Lease lease, int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        lock (lease.State.Gate)
        {
            var state = lease.State;
            if (state.Owner != this) throw new InvalidOperationException("Foreign lease.");
            if (!state.Attached || state.Generation != lease.Generation) return false;
            // Never credit unspent grants or an unsent receipt as peer-consumed bytes.
            // This model uses generation-tagged updates, NOT the key-only wire API.
            var returned = Math.Min(state.Outstanding, bytes);
            // Ordered model updates consume earlier committed bytes before this
            // writer's in-flight publication. The publication pin survives even when
            // all its bytes are credited before the writer reports completion.
            var earlier = state.Outstanding - state.PublicationUncredited;
            if (returned > earlier) state.PublicationUncredited -= checked((int)(returned - earlier));
            state.Outstanding -= returned;
            state.Credit += returned;
            _free += returned;
            TryRetireClosed(state);
            return true;
        }
    }

    private void Retire(State state)
    {
        if (state.PublicationActive || state.Pending != 0 || state.Outstanding != 0 || state.Grant != 0)
            throw new InvalidOperationException("Cannot recycle a state with live credit or publication ownership.");
        state.Attached = false;
        _states.Remove(state.Key);
        if (_pool.Count < _maxStreams) _pool.Push(state);
    }

    internal Task<bool> CloseAsync(Lease lease) => OnOwnerAsync(() =>
    {
        lock (lease.State.Gate)
        {
            var state = Validate(lease);
            if (state.Closed) return false;
            state.Closed = true;
            _free += state.Grant + state.Pending;
            state.Credit += state.Pending;
            state.Grant = state.Pending = 0;
            TryRetireClosed(state);
            return true;
        }
    });

    internal Task<Ledger> SnapshotAsync() => OnOwnerAsync(() =>
    {
        long grants = 0, pending = 0, outstanding = 0;
        var publications = 0;
        foreach (var state in _states.Values)
        {
            lock (state.Gate)
            {
                grants += state.Grant;
                pending += state.Pending;
                outstanding += state.Outstanding;
                if (state.PublicationActive) publications++;
                if (state.PublicationUncredited < 0 || state.PublicationUncredited > state.Outstanding ||
                    !state.PublicationActive && state.PublicationUncredited != 0 ||
                    state.PublicationActive && (state.Pending != 0 || state.PublicationBytes <= 0) ||
                    !state.PublicationActive && state.PublicationBytes != 0)
                    throw new InvalidOperationException("Publication ownership invariant failed.");
                if (state.Credit + state.Pending + state.Outstanding != _streamWindow)
                    throw new InvalidOperationException("Stream conservation failed.");
            }
        }
        if (_free + grants + pending + outstanding != _connectionWindow)
            throw new InvalidOperationException("Connection conservation failed.");
        return new Ledger(_free, grants, pending, outstanding, _waiters.Count, _states.Count, publications);
    });

    public async ValueTask DisposeAsync()
    {
        await OnOwnerAsync(() =>
        {
            Volatile.Write(ref _pressure, 1);
            Volatile.Write(ref _terminal, true);
            RevokeUnspent();
            foreach (var state in _states.Values)
                lock (state.Gate) state.Closed = true;
            return true;
        }).ConfigureAwait(false);
        _commands.Writer.TryComplete();
        await _owner.ConfigureAwait(false);
    }
}
