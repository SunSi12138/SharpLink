using System.Linq;
using SharpLink.Client;

namespace SharpLink.UnitTests.Builder;

public sealed partial class BuildPlanBuilderTests
{
    [Test]
    public async Task CoordinatorStopSignalsBeforeMetadataJoinButKeepsPhysicalOwnerAndCancellationFailure()
    {
        var initialTransport = new TrackingClientTransport();
        var incomingTransport = new TrackingClientTransport();
        var coordinator = (SharpLinkMultiClusterClient)SharpLinkMultiClusterClientBuilder.Create()
            .DisableRequestTimeout()
            .UseGeneratedDiscoverySources(FixedGeneratedManifestSource.Empty, FixedGeneratedClusterRouteSource.Empty)
            .AddCluster("initial", child => child.UseTransport(initialTransport), slot => slot.AllowDynamicContracts = true)
            .Build();
        var cancellationFailure = new InvalidOperationException("controlled shutdown callback failure");
        using var throwingCallback = coordinator.ShutdownTokenForDiagnostics.Register(() => throw cancellationFailure);
        using var source = new ShutdownSignalRouteSource(coordinator.ShutdownTokenForDiagnostics,
            () => coordinator.TryGetClusterStatus("initial", out _));
        using var waitCancellation = new CancellationTokenSource();
        var add = LongRunningTestWorker.Run(() => ((ISharpLinkMultiClusterLifecycleControl)coordinator).AddClusterAsync(
            "incoming", SharpClientBuilder.Create().DisableRequestTimeout().UseTransport(incomingTransport),
            true, CancellationToken.None, FixedGeneratedManifestSource.Empty, source).AsTask().GetAwaiter().GetResult());
        Task? physicalStop = null;
        Exception? assertionFailure = null;
        Exception? addFailure;
        Exception? stopFailure;
        try
        {
            Ensure(source.Entered.Wait(TimeSpan.FromSeconds(5)), "the admitted actual route callback must reach its shutdown wait");
            var callerWait = coordinator.StopAsync(waitCancellation.Token).AsTask();
            physicalStop = coordinator.StopAsync().AsTask();
            Ensure(source.ShutdownObserved.Wait(TimeSpan.FromSeconds(5)),
                "Stop must signal cancellation before joining a callback waiting on that signal");
            Ensure(source.StatusReadCompleted,
                "the cancellation callback must be able to enter the real coordinator Status path outside publication gate");
            Ensure(!physicalStop.IsCompleted && initialTransport.DisposeCount == 0,
                "shutdown signaling is not metadata completion: physical callback ownership must still block child cleanup");
            waitCancellation.Cancel();
            Ensure(await CaptureStopOrderingFailure(callerWait) is OperationCanceledException,
                "the caller's Stop wait budget must cancel without releasing the shared physical cleanup owner");
            Ensure(!physicalStop.IsCompleted && initialTransport.DisposeCount == 0,
                "an abandoned Stop wait must not clear the admitted metadata owner or dispose its child");
        }
        catch (Exception failure) { assertionFailure = failure; }
        finally
        {
            // This deterministic escape also unstrands the old signal-after-join implementation
            // when the source control fails, rather than leaving background fixture work blocked.
            source.Abort.Set();
            source.ReleasePhysical.Set();
            addFailure = await CaptureStopOrderingFailure(add.WaitAsync(TimeSpan.FromSeconds(5)));
            physicalStop ??= coordinator.StopAsync().AsTask();
            stopFailure = await CaptureStopOrderingFailure(physicalStop.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        if (assertionFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(assertionFailure).Throw();
        Ensure(addFailure is InvalidOperationException,
            "sealed admission must reject the unpublished child after the callback physically returns");
        Ensure(stopFailure is AggregateException aggregate &&
            aggregate.Flatten().InnerExceptions.Any(error => ReferenceEquals(error, cancellationFailure)),
            "cancellation callback errors must survive while later physical cleanup still completes");
        Ensure(initialTransport.DisposeCount == 1 && incomingTransport.DisposeCount == 1 &&
            coordinator.State == SharpLinkMultiClusterState.Stopped && source.Calls == 1,
            "both actual resource owners must clean up exactly once without replaying the admitted source");
    }

    private static async Task<Exception?> CaptureStopOrderingFailure(Task task)
    {
        try { await task.ConfigureAwait(false); return null; }
        catch (Exception exception) { return exception; }
    }

    private sealed class ShutdownSignalRouteSource(CancellationToken shutdown, Func<bool> inspectStatus)
        : IGeneratedClusterRouteSource, IDisposable
    {
        internal readonly ManualResetEventSlim Entered = new(false);
        internal readonly ManualResetEventSlim ShutdownObserved = new(false);
        internal readonly ManualResetEventSlim Abort = new(false);
        internal readonly ManualResetEventSlim ReleasePhysical = new(false);
        internal bool StatusReadCompleted;
        internal int Calls;
        public IReadOnlyList<ISharpLinkGeneratedClusterRouteManifest> CreateSnapshot()
        {
            Calls++;
            using var registration = shutdown.Register(() =>
            {
                StatusReadCompleted = inspectStatus();
                ShutdownObserved.Set();
            });
            Entered.Set();
            if (WaitHandle.WaitAny([ShutdownObserved.WaitHandle, Abort.WaitHandle]) == 1)
                throw new InvalidOperationException("controlled route callback abort");
            ReleasePhysical.Wait();
            return [];
        }
        public void Dispose()
        { Entered.Dispose(); ShutdownObserved.Dispose(); Abort.Dispose(); ReleasePhysical.Dispose(); }
    }
}
