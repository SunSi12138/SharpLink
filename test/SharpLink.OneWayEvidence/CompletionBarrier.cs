using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace SharpLink.OneWayEvidence;

// Investigation-only, in-process acknowledgement. The RPC itself remains OneWay.
// There is exactly one waiter per batch, and each batch drains before Reset.
internal sealed class CompletionBarrier(int batchSize) : IValueTaskSource
{
    private ManualResetValueTaskSourceCore<bool> _completion = new() { RunContinuationsAsynchronously = true };
    private readonly int[] _seen = new int[batchSize];
    private long _firstCall;
    private int _remaining;
    private Exception? _failure;
    internal long CompletedCalls;
    internal long ValidatedItems;

    internal ValueTask Begin(long firstCall)
    {
        _completion.Reset();
        _firstCall = firstCall;
        _remaining = batchSize;
        _failure = null;
        Array.Clear(_seen);
        return new ValueTask(this, _completion.Version);
    }

    internal void Complete(long callId, Exception? error, int items)
    {
        var slot = callId - _firstCall;
        if (slot < 0 || slot >= _seen.Length || Interlocked.Exchange(ref _seen[(int)slot], 1) != 0)
            error = new InvalidOperationException($"Duplicate or out-of-batch call: {callId}, first={_firstCall}.");
        if (error is not null)
            Interlocked.CompareExchange(ref _failure, error, null);
        else
        {
            Interlocked.Increment(ref CompletedCalls);
            Interlocked.Add(ref ValidatedItems, items);
        }
        if (Interlocked.Decrement(ref _remaining) == 0)
        {
            if (_failure is { } failure)
                _completion.SetException(failure);
            else
                _completion.SetResult(true);
        }
    }

    public void GetResult(short token) => _completion.GetResult(token);
    public ValueTaskSourceStatus GetStatus(short token) => _completion.GetStatus(token);
    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _completion.OnCompleted(continuation, state, token, flags);
}
