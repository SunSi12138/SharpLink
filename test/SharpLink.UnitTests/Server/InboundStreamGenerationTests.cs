using System.Reflection;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public partial class SharpLinkServerInvocationTests
{
    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StaleInboundCallCaptureShouldRetryCurrentGenerationWithoutTerminatingItsRoute(bool replaceState)
    {
        const long requestId = 9081;
        var calls = CreateCallCancellations();
        var first = ServerCallCancellationState.Rent(requestId, default, TimeProvider.System,
            CancellationToken.None, CancellationToken.None, supportsCooperativeCancellation: false);
        calls.Set(requestId, first);
        var stale = first.CaptureLease(requestId);
        Ensure(first.TryClaimResponse(), "old call must have completed");
        Ensure(calls.TryRemove(requestId, first), "remove old generation before pooling");
        first.Dispose();

        ServerCallCancellationState? current = null;
        if (replaceState)
        {
            current = ServerCallCancellationState.Rent(requestId, default, TimeProvider.System,
                CancellationToken.None, CancellationToken.None, supportsCooperativeCancellation: false);
            Ensure(ReferenceEquals(first, current), "exercise the same pooled object with a newer generation");
            calls.Set(requestId, current);
        }
        var manager = new StreamManager();
        manager.ReservePreAdmissionStreams(requestId, 1,
            new SharpLinkBufferWriterPool(new BufferWriterPoolOptions()), _ => true, _ => { },
            () => throw new Exception("unexpected stream capacity"), retainUntilLocalCompletion: true);
        var child = new GenerationRecordingDispatcher();
        manager.Register(requestId, 1, child);
        try
        {
            var method = typeof(SharpLinkServer).GetMethod("TryAcceptCapturedInboundStreamProgress",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            object?[] arguments = [calls, requestId, stale, false];
            var accepted = (bool)method.Invoke(null, arguments)!;
            Ensure(accepted && (bool)arguments[3]! == replaceState,
                "stale capture must inspect the current generation or use ordinary routing when absent");
            if (current is not null)
                Ensure(current.Reason == ServerCallCancellationReason.None,
                    "an old terminal must not affect the new call's cancellation state");
            await manager.DispatchChunkAsync(requestId, 1, new ReadOnlySequence<byte>(new byte[4]));
            Ensure(child.DispatchCount == 1 && child.CompleteCount == 0,
                "the current stable route must remain live after stale ownership capture");
        }
        finally
        {
            manager.CompleteRequestStreams(requestId, null);
            if (current is not null)
            {
                calls.TryRemove(requestId, current);
                current.Dispose();
            }
        }
    }

    [Test]
    [NotInParallel]
    public void PersistentlyUnacquirableInboundStateShouldFailClosedWithoutSpinning()
    {
        const long requestId = 9082;
        var calls = CreateCallCancellations();
        var state = ServerCallCancellationState.Rent(requestId, default, TimeProvider.System,
            CancellationToken.None, CancellationToken.None, supportsCooperativeCancellation: false);
        var stale = state.CaptureLease(requestId);
        calls.Set(requestId, state);
        state.Dispose();
        try
        {
            var method = typeof(SharpLinkServer).GetMethod("TryAcceptCapturedInboundStreamProgress",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            try
            {
                method.Invoke(null, [calls, requestId, stale, false]);
                throw new Exception("persistently retired ownership must fail closed");
            }
            catch (TargetInvocationException exception) when (
                exception.InnerException is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed })
            {
            }
        }
        finally
        {
            calls.TryRemove(requestId, state);
        }
    }

    private sealed class GenerationRecordingDispatcher : IStreamDispatcher
    {
        internal int DispatchCount { get; private set; }
        internal int CompleteCount { get; private set; }
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        {
            DispatchCount++;
            return ValueTask.CompletedTask;
        }
        public void Complete(bool isError, string? errorMessage) => CompleteCount++;
        public void Complete(Exception? exception) => CompleteCount++;
    }
}
