using System.Linq;

namespace SharpLink.UnitTests.Runtime;

// Private permits and in-memory environment only. These negative controls never
// release the production containment list or repair a real unknown host owner.
public sealed class RetirementTestIsolationScopeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Test]
    public Task ActualRunSnapshotFailureDoesNotLeakGrantedPermit()
        => RunControl(async control =>
    {
        var permit = control.Permit;
        var marker = new InvalidOperationException("first actual Run snapshot");
        var original = RetirementOwnedDriverTests.RunSnapshotFailureForDiagnostics(permit, _ => throw marker);
        var failure = await control.Failure(original);
        Exact(failure, marker);
        Ensure(permit.CurrentCount == 1, "actual Run returned its granted permit before host construction");
        control.Verified = true;
    });

    [Test]
    public Task SecondSnapshotFailureDoesNotMutateEitherEnvironmentValue()
        => RunControl(async control =>
    {
        var permit = control.Permit;
        var marker = new InvalidOperationException("second snapshot");
        var reads = 0; var writes = 0; var body = false; var cleanup = false;
        var scope = control.Scope = new RetirementTestIsolationScope(permit, Bound, false, true,
            () => { body = true; return Task.CompletedTask; },
            () => { cleanup = true; return Task.CompletedTask; }, () => true,
            _ => ++reads == 2 ? throw marker : "previous",
            (_, _) => writes++);
        Exact(await control.Failure(scope.ExecuteAsync()), marker);
        Ensure(reads == 2 && writes == 0 && !body && cleanup && scope.PermitReleased && permit.CurrentCount == 1,
            "both snapshots precede writes and partial setup still closes scope");
        control.Verified = true;
    });

    [Test]
    public Task MutatingSecondSetterFailureRestoresBothSnapshots()
        => RunControl(async control =>
    {
        var permit = control.Permit;
        var values = Values(); var marker = new InvalidOperationException("mutate then fail");
        var writes = 0; var body = false;
        var scope = control.Scope = new RetirementTestIsolationScope(permit, Bound, false, true,
            () => { body = true; return Task.CompletedTask; }, () => Task.CompletedTask, () => true,
            name => values[name], (name, value) =>
            {
                values[name] = value;
                if (++writes == 2) throw marker;
            });
        Exact(await control.Failure(scope.ExecuteAsync()), marker);
        Ensure(!body && writes == 4 && Restored(values) && scope.PermitReleased && permit.CurrentCount == 1,
            "attempted setter is restored even when it throws after mutation");
        control.Verified = true;
    });

    [Test]
    public Task BodyCleanupAndBothRestoreFailuresKeepFourOccurrencesAndRoot()
        => RunControl(async control =>
    {
        var permit = control.Permit;
        var values = Values(); var marker = new InvalidOperationException("four independent stages");
        var roots = control.ScopeRoots; var writes = 0;
        var scope = control.Scope = new RetirementTestIsolationScope(permit, Bound, false, true,
            () => throw marker, () => throw marker, () => true, name => values[name],
            (name, value) => { if (++writes > 2) throw marker; values[name] = value; }, retain: roots.Add);
        Exact(await control.Failure(scope.ExecuteAsync()), marker, marker, marker, marker);
        Ensure(writes == 4 && scope.IsRetained && !scope.PermitReleased && permit.CurrentCount == 0 &&
            roots.Count == 1 && ReferenceEquals(roots[0], scope), "both restore attempts occur, scope remains rooted");

        control.Verified = true;
    });

    [Test]
    public Task PendingOriginalOwnerPreventsRestoreAndPermitRelease()
        => RunControl(async control =>
    {
        var permit = control.Permit;
        var values = Values();
        var source = control.PendingSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var originalCleanup = source.Task; var writes = 0; var cleanupEntered = false;
        var scope = control.Scope = new RetirementTestIsolationScope(permit, Bound, false, true,
            () => Task.CompletedTask,
            () => { cleanupEntered = true; return originalCleanup; },
            () => originalCleanup.IsCompleted, name => values[name],
            (name, value) => { writes++; values[name] = value; }, retain: control.ScopeRoots.Add);
        var operation = scope.ExecuteAsync();
        var outcome = control.Observe(operation);
        Ensure(cleanupEntered && !originalCleanup.IsCompleted && !operation.IsCompleted && !outcome.IsCompleted &&
            writes == 2 && permit.CurrentCount == 0 && !scope.PermitReleased,
            "actual cleanup task is pending; no restoration or refund");
        Exception? diagnostic = null;
        try { await outcome.WaitAsync(TimeSpan.Zero); }
        catch (Exception failure) { diagnostic = failure; }
        // Outcome is success-only data. This exception belongs exclusively to
        // the deliberately zero diagnostic wait, not to the original source.
        Ensure(diagnostic?.GetType() == typeof(TimeoutException), "exact deliberate diagnostic timeout");
        control.ExpectedDiagnostics.Add(diagnostic!);
        Ensure(control.IsDurablyRooted && !operation.IsCompleted && !outcome.IsCompleted &&
            !originalCleanup.IsCompleted && writes == 2 && permit.CurrentCount == 0,
            "timeout keeps scope, actual cleanup/operation/outcome and permit reachable");
        source.SetResult();
        await originalCleanup;
        Ensure(await outcome.WaitAsync(Bound) is null, "real cleanup completion independently joins original scope");
        Ensure(scope.PermitReleased && writes == 4 && Restored(values) && permit.CurrentCount == 1,
            "only actual cleanup completion permits normal restoration/refund");

        control.Verified = true;
    });

    [Test]
    public Task PermitReleaseFailurePreservesBodyAndItsScopeRoot()
        => RunControl(async control =>
    {
        var permit = control.Permit;
        var values = Values(); var roots = control.ScopeRoots;
        var body = new InvalidOperationException("body"); var release = new InvalidOperationException("release");
        var scope = control.Scope = new RetirementTestIsolationScope(permit, Bound, false, true,
            () => throw body, () => Task.CompletedTask, () => true,
            name => values[name], (name, value) => values[name] = value,
            release: () => throw release, retain: roots.Add);
        Exact(await control.Failure(scope.ExecuteAsync()), body, release);
        Ensure(Restored(values) && scope.IsRetained && !scope.PermitReleased &&
            roots.Count == 1 && ReferenceEquals(roots[0], scope) && permit.CurrentCount == 0,
            "restoration succeeds but independent refund failure does not erase body or roots");

        control.Verified = true;
    });

    private static Dictionary<string, string?> Values() => new()
    {
        [RetirementTestIsolationScope.CodecVariable] = "old-codec",
        [RetirementTestIsolationScope.ContractVariable] = "old-contract"
    };
    private static bool Restored(Dictionary<string, string?> values)
        => values[RetirementTestIsolationScope.CodecVariable] == "old-codec" &&
            values[RetirementTestIsolationScope.ContractVariable] == "old-contract";
    private static readonly List<Control> Containment = [];

    private static async Task RunControl(Func<Control, Task> body)
    {
        var control = new Control();
        // This root precedes any scope invocation. Failure/timeout cannot make
        // local scopes, original tasks, outcome observers or permits disappear.
        lock (Containment) Containment.Add(control);
        try { await body(control); }
        catch (Exception failure) { control.Failures.Add(failure); }
        finally
        {
            try { await control.Close(); }
            catch (Exception failure) { control.Failures.Add(failure); }
        }
        if (control.Failures.Count != 0) throw new AggregateException(control.Failures);
    }

    private sealed class Control
    {
        internal readonly SemaphoreSlim Permit = new(1, 1);
        internal readonly List<RetirementTestIsolationScope> ScopeRoots = [];
        internal readonly List<Exception> Failures = [];
        internal readonly List<Exception> ExpectedDiagnostics = [];
        internal bool IsDurablyRooted { get { lock (Containment) return Containment.Contains(this); } }
        internal RetirementTestIsolationScope? Scope;
        internal TaskCompletionSource? PendingSource;
        internal bool Verified;
        private readonly List<(Task Original, Task<Exception?> Outcome)> _operations = [];

        internal Task<Exception?> Observe(Task original)
        {
            foreach (var pair in _operations)
                if (ReferenceEquals(pair.Original, original)) return pair.Outcome;
            static async Task<Exception?> ObserveOriginal(Task task)
            { try { await task; return null; } catch (Exception failure) { return failure; } }
            var outcome = ObserveOriginal(original);
            _operations.Add((original, outcome));
            return outcome;
        }

        internal async Task<Exception> Failure(Task original)
        {
            var outcome = Observe(original);
            // A diagnostic timeout remains a failure even if the original later
            // finishes. Static Control containment owns both actual tasks.
            return await outcome.WaitAsync(Bound) ?? throw new InvalidOperationException("Expected original failure.");
        }

        internal async Task Close()
        {
            // This is exclusively the test-created source, not a product source.
            // Always release its real signal, including after assertion failure.
            if (PendingSource is { } pending)
            {
                pending.TrySetResult();
                await pending.Task;
            }
            var ended = _operations.All(pair => pair.Original.IsCompleted && pair.Outcome.IsCompleted) &&
                (PendingSource?.Task.IsCompleted ?? true);
            if (!ended || !Verified || Failures.Count != 0)
            {
                if (Failures.Count == 0)
                    Failures.Add(new InvalidOperationException("Negative control did not establish actual exit and its full oracle."));
                return;
            }
            // Only a completely asserted fake control may repair its private
            // permit. Actual Run (Scope null) must already have refunded it.
            if (Permit.CurrentCount == 0 && Scope is { IsRetained: true }) Permit.Release();
            Ensure(Permit.CurrentCount == 1, "verified private permit must be free before disposal");
            Permit.Dispose();
            ScopeRoots.Clear();
            lock (Containment) Containment.Remove(this);
        }
    }

    private static void Exact(Exception failure, params Exception[] expected)
    {
        Ensure(failure is AggregateException, "scope reports independent failures");
        var actual = ((AggregateException)failure).InnerExceptions;
        Ensure(actual.Count == expected.Length && actual.Zip(expected).All(pair => ReferenceEquals(pair.First, pair.Second)),
            "exact ordered raw occurrence identities, no flattening or deduplication");
    }
    private static void Ensure(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
