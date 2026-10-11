namespace SharpLink.Runtime;

internal sealed partial class WriterOwnedStreamScheduler
{
    internal void AbortRequest(long requestId, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        StreamHandle[] streams;
        lock (_mapGate)
        {
            var matching = new List<StreamHandle>();
            foreach (var stream in _streams.Values) if (stream.RequestId == requestId) matching.Add(stream);
            streams = matching.ToArray();
        }
        foreach (var stream in streams) Abort(stream, error, remote: true);
    }

    internal void Abort(StreamHandle stream, Exception error, bool remote = false)
    {
        ArgumentNullException.ThrowIfNull(error);
        ValidateOwner(stream);
        List<PreparedFrame>? discarded = null;
        List<DrainWaiter>? drains;
        TaskCompletionSource? space;
        lock (stream.Gate)
        {
            if (!stream.Attached) return;
            stream.Error ??= error;
            stream.RemoteAbort |= remote;
            while (stream.Frames.TryDequeue(out var frame)) (discarded ??= []).Add(frame);
            space = stream.SpaceChanged;
            stream.SpaceChanged = null;
            drains = stream.DrainWaiters;
            stream.DrainWaiters = null;
        }
        // A wake hint never faults. A canceled WaitAsync proxy may have detached from it;
        // waking and rechecking avoids leaving an unobserved exception on the abandoned hint.
        space?.TrySetResult();
        CompleteDrainWaiters(drains, error);
        _prepared.CompleteStream(stream.RequestId, stream.StreamId, error);
        ReturnDiscarded(discarded);
        Notify(stream);
    }

    private void ReturnDiscarded(List<PreparedFrame>? frames)
    {
        if (frames is null) return;
        foreach (var frame in frames)
        {
            try { _returnBuffer(frame.Packet); }
            catch (Exception error) { Interlocked.CompareExchange(ref _cleanupFailure, error, null); }
            finally { _prepared.Release(frame.Bytes); }
        }
    }

    internal Task DrainAcceptedFramesAsync(StreamHandle stream, CancellationToken cancellationToken = default)
    {
        ValidateOwner(stream);
        Task wait;
        lock (stream.Gate)
        {
            if (stream.Error is { } error) return Task.FromException(error);
            if (stream.ReleasedFrames == stream.AcceptedFrames) return Task.CompletedTask;
            var waiter = new DrainWaiter(stream.AcceptedFrames);
            (stream.DrainWaiters ??= []).Add(waiter);
            wait = waiter.Completion.Task;
        }
        return cancellationToken.CanBeCanceled ? WaitForDrainAsync(wait, cancellationToken) : wait;
    }

    private static async Task WaitForDrainAsync(Task source, CancellationToken token)
    {
        try { await source.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _ = ObserveAbandonedDrainAsync(source);
            throw;
        }
    }

    private static async Task ObserveAbandonedDrainAsync(Task source)
    {
        try { await source.ConfigureAwait(false); }
        catch { }
    }

    private static void CompleteDrainWaiters(List<DrainWaiter>? waiters, Exception? error)
    {
        if (waiters is null) return;
        foreach (var waiter in waiters)
            if (error is null) waiter.Completion.TrySetResult(); else waiter.Completion.TrySetException(error);
    }

    private void StopPreparing(Exception error)
    {
        if (Interlocked.CompareExchange(ref _closing, error, null) is not null) return;
        _prepared.Complete(error);
        StreamHandle[] streams;
        lock (_mapGate)
        {
            streams = new StreamHandle[_streams.Count];
            _streams.Values.CopyTo(streams, 0);
            _capacityChanged?.TrySetResult();
            _capacityChanged = null;
        }
        foreach (var stream in streams) Abort(stream, error);
    }

    public void Stopped(Exception error)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        StopPreparing(error);
        // Join the preparation-only cancellation callback outside all stream/map gates.
        _cancellation.Dispose();
        lock (_mapGate)
        {
            foreach (var stream in _streams.Values)
            {
                lock (stream.Gate)
                {
                    stream.Attached = false;
                    CompleteDrainWaiters(stream.DrainWaiters, error);
                    stream.DrainWaiters = null;
                }
            }
            _streams.Clear();
        }
        _ready.Clear();
        while (_changes.TryDequeue(out _)) { }
    }
}
