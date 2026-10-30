namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<ISharpLinkMaintenanceOperation> CompleteCallbackMaintenanceAsync(MaintenanceHarness harness)
    {
        var contract = harness.Plugin.ContractAssembly.GetType("SharpLink.DynamicPlugin.IMaintenanceNotificationProbe", true)!;
        var implementation = harness.SourceAssembly.GetType("SharpLink.DynamicPlugin.MaintenanceNotificationProbe", true)!;
        Task Signal(string name) => (Task)implementation.GetProperty(name)!.GetValue(null)!;
        void Release(string name) => implementation.GetMethod(name)!.Invoke(null, null);
        int Count(string name) => (int)implementation.GetProperty(name)!.GetValue(null)!;
        var sendData = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = (SharpLinkServer)harness.Server;
        var connections = server.CaptureMaintenanceCohort();
        using var cancelled = new CancellationTokenSource();
        var call = InvokeValueTaskAsync<int>(harness.HeldNotifications[0], contract,
            "ReleaseBeforeCancellationCallbackAsync", DeferredMaintenanceInput(sendData.Task), cancelled.Token).AsTask();
        try
        {
            await Signal("InvocationEntered").WaitAsync(TimeSpan.FromSeconds(10));
            var deadlines = new bool[16];
            Ensure(connections.Any(connection => connection.CallCancellations.CopyEntries<bool>(deadlines,
                static (_, state) =>
                {
                    var deadline = state.GetType().GetProperty("Deadline")!.GetValue(state)!;
                    return deadline.GetType().GetProperty("HasValue",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(deadline) is true;
                }) > 0 && deadlines.Any(static timed => timed)),
                "streaming method explicitly selects the configured wire deadline");
            // Only the monotonic test clock advances, not real timer callbacks. The deadline
            // scan remains scheduled an hour away; this StreamData drives deferred notification.
            harness.Clock.AdvanceMonotonicWithoutRunningTimers(TimeSpan.FromHours(2));
            foreach (var connection in connections) connection.Session.MarkActive();
            sendData.TrySetResult();
            await Signal("CallbackEntered").WaitAsync(TimeSpan.FromSeconds(10));
            cancelled.Cancel();
            try { await call.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
            catch (SharpLinkException exception) when (exception.Code is SharpLinkErrorCode.Cancelled or SharpLinkErrorCode.DeadlineExceeded) { }
            Release("ReleaseInvocation");
            await WaitForMaintenanceAsync(() => !server.HasMaintenanceOwners() && connections.All(static connection =>
                connection.ActiveCalls == 0 && connection.CallCancellations.Count == 0 && connection.Session.StreamManager.ActiveStreamCount == 0));
            Ensure(!Signal("CallbackExited").IsCompleted, "original registered callback outlives ordinary invocation/map/stream owners");
            var callbackDrains = connections.Select(static connection => connection.DrainPhysicalCancellationCallbacksAsync()).ToArray();
            Ensure(callbackDrains.Any(static drain => !drain.IsCompleted),
                "existing physical callback ownership remains visible after ordinary owners reach zero");
            var operation = harness.Begin();
            await WaitForMaintenanceAsync(() => operation.Stage >= SharpLinkMaintenanceStage.Draining);
            await Task.WhenAny(operation.WaitAsync(), Task.Delay(150));
            Ensure(operation.Stage == SharpLinkMaintenanceStage.Draining && !operation.WaitAsync().IsCompleted,
                "physical callback owner prevents retirement after the full ingress fence");
            Ensure(Count("Disposed") == 0, "old implementation disposal cannot overlap callback execution");
            Release("ReleaseCallback");
            await Signal("CallbackExited").WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(callbackDrains).WaitAsync(TimeSpan.FromSeconds(10));
            var result = await operation.WaitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Ensure(result.Stage == SharpLinkMaintenanceStage.Succeeded && Count("Disposed") > 0 && Count("OverlappingDisposal") == 0,
                "callback completes before successful physical replacement and owned disposal");
            return operation;
        }
        finally
        {
            sendData.TrySetResult();
            cancelled.Cancel();
            Release("ReleaseInvocation");
            Release("ReleaseCallback");
        }
    }

    private static async IAsyncEnumerable<int> DeferredMaintenanceInput(Task release,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await release.WaitAsync(cancellationToken).ConfigureAwait(false);
        yield return 1;
    }
}
