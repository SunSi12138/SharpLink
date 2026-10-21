namespace SharpLink.IntegrationTests;

public sealed partial class ColdSessionFenceResearchTests
{
    [Test]
    [NotInParallel]
    public async Task CompilerIteratorConcurrentDisposalIsNotRetriedAfterAbandonedMoveNextYieldsTrue()
    {
        ColdFenceResearchService.Reset();
        await using var harness = await Harness.CreateAsync(admission: true, freezeClientClock: true);
        await using var reader = harness.Client.Get<IColdFenceResearchService>()
            .CompilerDeadlineDownloadAsync(CancellationToken.None).GetAsyncEnumerator();
        try
        {
            Ensure(await reader.MoveNextAsync().AsTask().WaitAsync(PhaseTimeout), "initial compiler stream element");
            await ColdFenceResearchService.Entered.WaitAsync(PhaseTimeout);
            var next = reader.MoveNextAsync().AsTask();
            await using var pause = await ResearchPause.AcquireAsync(harness.Client);
            // Let the server's own deadline win before the client emits its deadline Cancel.
            // Then expire the client clock and apply the full client-drain/fence/server predicate.
            await WaitForServerDrainAsync(harness.Server);
            harness.ExpireClientDeadline();
            Exception? failure = null;
            try { await next.WaitAsync(PhaseTimeout); }
            catch (Exception exception) { failure = exception; }
            Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded }, "compiler stream deadline");
            await WaitForClientDrainAsync(harness.Connection);
            await FenceAsync(harness.Connection);
            await WaitForServerDrainAsync(harness.Server);
            Ensure(ColdFenceResearchService.CompilerDisposalException is NotSupportedException &&
                   ColdFenceResearchService.CompilerDisposeCalls == 1,
                "framework attempts DisposeAsync concurrently with outstanding compiler MoveNext, which rejects it");
            Ensure(!ColdFenceResearchService.CleanupEntered.IsCompleted,
                "failed concurrent disposal cannot enter compiler iterator finally");
            ColdFenceResearchService.ReleaseWork();
            Ensure(await ColdFenceResearchService.CompilerMoveNextReturned.WaitAsync(PhaseTimeout),
                "the abandoned MoveNext eventually returns true, leaving the compiler iterator suspended at yield");
            await FenceAsync(harness.Connection);
            Ensure(ColdFenceResearchService.CompilerDisposeCalls == 1 &&
                   !ColdFenceResearchService.CleanupEntered.IsCompleted && IsServerDrained(harness.Server),
                "the observer consumes MoveNext but performs no second MoveNext or Dispose; finally remains unentered");

            // Explicit test cleanup, NOT an operation performed by the framework. Its necessity
            // distinguishes lost compiler-finally cleanup from merely delayed tracked cleanup.
            var manualCleanup = ColdFenceResearchService.DisposeCompilerProbeAsync().AsTask();
            await ColdFenceResearchService.CleanupEntered.WaitAsync(PhaseTimeout);
            Ensure(!manualCleanup.IsCompleted && ColdFenceResearchService.CompilerDisposeCalls == 2,
                "only the test's second disposal enters async finally");
            ColdFenceResearchService.ReleaseCleanup();
            await manualCleanup.WaitAsync(PhaseTimeout);
            await ColdFenceResearchService.Completed.WaitAsync(PhaseTimeout);
        }
        finally
        {
            ColdFenceResearchService.ReleaseAll();
        }
    }
}

public sealed partial class ColdFenceResearchService
{
    private static TaskCompletionSource<bool> s_compilerMoveNextReturned = NewCompilerSignal();
    private static Exception? s_compilerDisposalException;
    private static int s_compilerDisposeCalls;
    private static CompilerIteratorObserver? s_compilerIterator;
    internal static Task<bool> CompilerMoveNextReturned => s_compilerMoveNextReturned.Task;
    internal static Exception? CompilerDisposalException => Volatile.Read(ref s_compilerDisposalException);
    internal static int CompilerDisposeCalls => Volatile.Read(ref s_compilerDisposeCalls);
    internal static ValueTask DisposeCompilerProbeAsync() => s_compilerIterator!.DisposeAsync();
    private static void ResetCompilerProbe()
    {
        s_compilerMoveNextReturned = NewCompilerSignal();
        s_compilerDisposalException = null;
        s_compilerDisposeCalls = 0;
        s_compilerIterator = null;
    }
    private static TaskCompletionSource<bool> NewCompilerSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IAsyncEnumerable<int> CompilerDeadlineDownloadAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return s_compilerIterator = new CompilerIteratorObserver(CompilerIterator().GetAsyncEnumerator());
    }

    private static async IAsyncEnumerable<int> CompilerIterator()
    {
        try
        {
            yield return 1;
            s_entered.TrySetResult();
            await s_workRelease.Task;
            yield return 2;
        }
        finally
        {
            s_cleanupEntered.TrySetResult();
            await s_cleanupRelease.Task;
            s_completed.TrySetResult();
        }
    }

    private sealed class CompilerIteratorObserver(IAsyncEnumerator<int> inner)
        : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private int _moveCount;
        public int Current => inner.Current;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public async ValueTask<bool> MoveNextAsync()
        {
            var result = await inner.MoveNextAsync();
            if (++_moveCount == 2)
                s_compilerMoveNextReturned.TrySetResult(result);
            return result;
        }
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref s_compilerDisposeCalls);
            try { await inner.DisposeAsync(); }
            catch (Exception exception)
            {
                Volatile.Write(ref s_compilerDisposalException, exception);
                throw;
            }
        }
    }
}
