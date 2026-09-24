using System.Reflection;

namespace SharpLink.UnitTests.Runtime;

public partial class StreamManagerTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LastReleaseMustNotRenotifyAfterDrainContinuationDetaches(bool throwOnDrain)
    {
        var failure = new InvalidOperationException("controlled drain callback failure");
        var dispatcher = new DrainOwnershipDispatcher(throwOnDrain ? failure : null);
        var manager = new StreamManager();
        manager.Register(9301, dispatcher);
        var dispatch = manager.DispatchChunkAsync(9301,
            new ReadOnlySequence<byte>(new byte[] { 1 })).AsTask();

        // Deterministically schedule the legitimate drain -> Detach continuation before
        // Release resumes after signaling it. Production's asynchronous continuation can
        // reach the same point on another worker. No production hook or timer is needed.
        ForceInlineDrainContinuation(dispatcher.State);
        var drain = manager.CompleteStreamAfterDispatchesAsync(9301, 0, null).AsTask();
        Ensure(!drain.IsCompleted, "the outstanding dispatch must keep drain pending");
        dispatcher.Release();
        Exception? dispatchFailure = null;
        Exception? drainFailure = null;
        try { await dispatch.WaitAsync(RaceCoordinationTimeout); }
        catch (Exception exception) { dispatchFailure = exception; }
        try { await drain.WaitAsync(RaceCoordinationTimeout); }
        catch (Exception exception) { drainFailure = exception; }

        Ensure(dispatchFailure is null,
            "the last release must not invoke a callback already owned by drain finalization");
        Ensure(ReferenceEquals(drainFailure, throwOnDrain ? failure : null),
            "drain finalization alone must observe the exact callback error");
        Ensure(dispatcher.DrainCalls == 1 && dispatcher.State.IsDetached,
            "the detached dispatcher must be notified exactly once, including a throwing callback");
    }

    private static void ForceInlineDrainContinuation(IStreamDispatchState state)
    {
        _ = state.WaitForDispatchesDrainedAsync(); // materialize the lazy completion holder
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var holder = state.GetType().GetField("_completions", flags)?.GetValue(state)
            ?? throw new InvalidOperationException("Expected the lazy drain completion holder.");
        var field = holder.GetType().GetField("_dispatchesDrainedCompletion", flags)
            ?? throw new InvalidOperationException("Expected the drain completion field.");
        field.SetValue(holder, new TaskCompletionSource());
    }

    private sealed class DrainOwnershipDispatcher(Exception? failure) : IStreamDispatcher, IStreamDispatchLease
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _drainCalls;
        internal IStreamDispatchState State { get; private set; } = null!;
        internal int DrainCalls => Volatile.Read(ref _drainCalls);
        internal void Release() => _release.TrySetResult();
        public void BindDispatchState(IStreamDispatchState state) => State = state;
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => new(_release.Task);
        public ValueTask DispatchAcquiredAsync(ReadOnlySequence<byte> payload, int encodedByteCount) => DispatchAsync(payload);
        public void Complete(bool isError, string? errorMessage) { }
        public void Complete(Exception? exception) { }
        public void OnDispatchesDrained()
        {
            Interlocked.Increment(ref _drainCalls);
            if (failure is not null) throw failure;
        }
    }
}
