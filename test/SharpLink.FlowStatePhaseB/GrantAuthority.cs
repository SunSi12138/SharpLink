using System.Threading.Channels;

namespace SharpLink.FlowStatePhaseB;

// Isolated SEND-SIDE model, not a replacement for StreamFlowController.
// One unsettled item receipt / one pending acquire per stream. Receive batching,
// wire integration and pooled queue completions are deliberately not claimed.
internal sealed class GrantAuthority : IAsyncDisposable
{
    private readonly int _streamWindow;
    private readonly int _connectionWindow;
    private readonly int _grantBytes;
    private readonly int _maxStreams;
    private readonly int _maxItemBytes;
    private readonly Channel<Action> _commands;
    private readonly Task _owner;
    private readonly Dictionary<long, State> _states = [];
    private readonly Stack<State> _pool = [];
    private readonly LinkedList<Request> _waiters = [];
    private long _generation;
    private long _free;
    private int _pressure;
    private bool _terminal;
    internal long QueueSubmissions;
    internal long AcquireSubmissions;
    internal long Revocations;
    internal long AdmittedWaiters;

    internal GrantAuthority(int streamWindow, int connectionWindow, int grantBytes,
        int maxStreams = 128, int maxItemBytes = 4 * 1024 * 1024)
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
        _commands = Channel.CreateBounded<Action>(new BoundedChannelOptions(checked(maxStreams * 4))
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
        long Outstanding, int Waiters, int Retained);

    internal sealed class State(GrantAuthority owner)
    {
        internal readonly GrantAuthority Owner = owner;
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
    }

    private sealed record Request(Lease Lease, int Bytes, CancellationToken Token,
        TaskCompletionSource<Receipt> Completion);

    private async Task RunOwnerAsync()
    {
        await foreach (var command in _commands.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            command();
            DrainWaiters();
        }
    }

    private async ValueTask SubmitAsync(Action command)
    {
        await _commands.Writer.WriteAsync(command).ConfigureAwait(false);
        Interlocked.Increment(ref QueueSubmissions);
    }

    private async Task<T> OnOwnerAsync<T>(Func<T> operation)
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
            state.Key = key;
            state.Generation = checked(++_generation);
            state.Attached = true;
            state.Closed = false;
            state.AcquirePending = false;
            state.Credit = _streamWindow;
            state.Grant = state.Outstanding = state.Sequence = 0;
            state.Pending = 0;
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
            if (state.Pending != 0 || state.AcquirePending)
                throw new InvalidOperationException("Settle the previous receipt/acquire first.");
            if (Volatile.Read(ref _pressure) == 0 && state.Grant >= bytes && state.Credit >= bytes)
            {
                state.Grant -= bytes;
                state.Credit -= bytes;
                state.Pending = bytes;
                return ValueTask.FromResult(new Receipt(lease, ++state.Sequence, bytes));
            }
            state.AcquirePending = true;
        }
        return AcquireOnOwnerAsync(lease, bytes, token);
    }

    private async ValueTask<Receipt> AcquireOnOwnerAsync(Lease lease, int bytes, CancellationToken token)
    {
        var completion = new TaskCompletionSource<Receipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new Request(lease, bytes, token, completion);
        using var registration = token.Register(() =>
        {
            // A full queue already guarantees an owner turn and cancellation scan.
            if (_commands.Writer.TryWrite(static () => { }))
                Interlocked.Increment(ref QueueSubmissions);
        });
        try
        {
            Interlocked.Increment(ref AcquireSubmissions);
            await SubmitAsync(() => AdmitOrQueue(request)).ConfigureAwait(false);
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (lease.State.Gate)
                if (lease.State.Generation == lease.Generation)
                    lease.State.AcquirePending = false;
        }
    }

    private bool HasCredit(long credit, int bytes, int window)
        => credit >= bytes || bytes > window && credit == window;

    private Receipt Reserve(Request request, bool extras)
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

    private void AdmitOrQueue(Request request)
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
                    request.Completion.SetResult(Reserve(request, extras: true));
                    return;
                }
            }
            // Freeze new local admissions before reclaiming any grant. Taking every
            // stream gate drains in-progress local debit/settlement operations.
            Volatile.Write(ref _pressure, 1);
            _waiters.AddLast(request);
            RevokeUnspent();
        }
        catch (OperationCanceledException) { request.Completion.TrySetCanceled(request.Token); }
        catch (Exception error) { request.Completion.TrySetException(error); }
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
                    request.Completion.TrySetCanceled(request.Token);
                    _waiters.Remove(canceled);
                }
                else if (_terminal || state.Closed || !state.Attached || state.Generation != request.Lease.Generation)
                {
                    request.Completion.TrySetException(new InvalidOperationException("Terminal or stale waiter."));
                    _waiters.Remove(canceled);
                }
            }
            canceled = next;
        }
        var node = _waiters.First;
        while (node is not null)
        {
            var next = node.Next;
            var request = node.Value;
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
                        var receipt = Reserve(request, extras: false);
                        AdmittedWaiters++;
                        request.Completion.TrySetResult(receipt);
                    }
                }
                catch (OperationCanceledException) { request.Completion.TrySetCanceled(request.Token); }
                catch (Exception error) { request.Completion.TrySetException(error); }
            }
            if (removed) _waiters.Remove(node);
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

    internal Task<bool> WindowUpdateAsync(Lease lease, int bytes) => OnOwnerAsync(() =>
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
            state.Outstanding -= returned;
            state.Credit += returned;
            _free += returned;
            if (state.Closed && state.Outstanding == 0) Retire(state);
            return true;
        }
    });

    private void Retire(State state)
    {
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
            if (state.Outstanding == 0) Retire(state);
            return true;
        }
    });

    internal Task<Ledger> SnapshotAsync() => OnOwnerAsync(() =>
    {
        long grants = 0, pending = 0, outstanding = 0;
        foreach (var state in _states.Values)
        {
            lock (state.Gate)
            {
                grants += state.Grant;
                pending += state.Pending;
                outstanding += state.Outstanding;
                if (state.Credit + state.Pending + state.Outstanding != _streamWindow)
                    throw new InvalidOperationException("Stream conservation failed.");
            }
        }
        if (_free + grants + pending + outstanding != _connectionWindow)
            throw new InvalidOperationException("Connection conservation failed.");
        return new Ledger(_free, grants, pending, outstanding, _waiters.Count, _states.Count);
    });

    public async ValueTask DisposeAsync()
    {
        await OnOwnerAsync(() =>
        {
            Volatile.Write(ref _pressure, 1);
            _terminal = true;
            RevokeUnspent();
            foreach (var state in _states.Values)
                lock (state.Gate) state.Closed = true;
            return true;
        }).ConfigureAwait(false);
        _commands.Writer.TryComplete();
        await _owner.ConfigureAwait(false);
    }
}
