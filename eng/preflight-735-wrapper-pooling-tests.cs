using System.Reflection;
using System.Threading.Tasks.Sources;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class ClientStreamWaitAllocationTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const int Warmups = 128;
    private const int Count = 512;

    [Test]
    [Arguments("logical", false)]
    [Arguments("telemetry", false)]
    [Arguments("both", false)]
    [Arguments("logical", true)]
    [Arguments("telemetry", true)]
    [Arguments("both", true)]
    public async Task RepeatedReadsDoNotAllocateOneWrapperPerItem(string wrapper, bool completed)
    {
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        var source = new ReusableStream { CompleteImmediately = completed };
        var reader = Wrap(client, source, wrapper);
        long allocated = 0;
        try
        {
            for (var iteration = 0; iteration < Warmups + Count; iteration++)
            {
                // Completion and consumption are inline. Measure the whole operation,
                // not only its initial allocation on a thread that later changes.
                var before = GC.GetAllocatedBytesForCurrentThread();
                var move = reader.MoveNextAsync();
                Require(move.IsCompleted == completed, "read did not take the declared completion path");
                if (!completed) source.Finish(true);
                Require(move.IsCompletedSuccessfully, "inline completion unexpectedly queued a continuation");
                Require(move.GetAwaiter().GetResult(), "a successful read lost its item");
                var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                if (iteration >= Warmups) allocated += bytes;
            }
            Require(source.ConsumptionCount == Warmups + Count, "a reusable source was consumed more than once");
            Require(source.DisposeCount == 0, "an item prematurely disposed the source");
            AssertLogicalCount(client, wrapper, 1);
            source.CompleteImmediately = false;
            var terminal = reader.MoveNextAsync();
            Require(!terminal.IsCompleted, "terminal control must genuinely suspend");
            source.Finish(false);
            Require(!terminal.GetAwaiter().GetResult(), "terminal result invented an item");
            AssertLogicalCount(client, wrapper, 0);
            Console.WriteLine($"CLIENT_WAIT_ALLOCATION wrapper={wrapper} completed={completed} count={Count} bytes={allocated}");
            Require(allocated <= (completed ? 0 : Count * 16L),
                "client MoveNext still allocates one fresh wrapper for every suspended read, or changed the zero-allocation fast path");
        }
        finally
        {
            source.ReleaseForCleanup();
            await reader.DisposeAsync();
        }
        Require(source.DisposeCount == 1, "source disposal must occur once");
        AssertLogicalCount(client, wrapper, 0);
    }

    [Test]
    [Arguments("logical", false)]
    [Arguments("telemetry", false)]
    [Arguments("both", false)]
    [Arguments("logical", true)]
    [Arguments("telemetry", true)]
    [Arguments("both", true)]
    public async Task SuspendedFailurePreservesIdentityAndCompletesAccounting(string wrapper, bool canceled)
    {
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Exception expected = canceled
            ? new OperationCanceledException("wrapper canceled", stop.Token)
            : new InvalidOperationException("wrapper fault");
        var source = new ReusableStream();
        var reader = Wrap(client, source, wrapper);
        try
        {
            var move = reader.MoveNextAsync();
            Require(!move.IsCompleted, "failure control must genuinely suspend");
            // The sole conversion consumes the ValueTask source; all later accesses
            // use this Task, as the production deadline observer does.
            var task = move.AsTask();
            source.Fail(expected);
            Require(task.IsCompleted, "inline failure unexpectedly queued a continuation");
            Exception? observed = null;
            try { _ = task.GetAwaiter().GetResult(); }
            catch (Exception error) { observed = error; }
            if (canceled)
                Require(observed is OperationCanceledException error && error.CancellationToken == stop.Token
                    && task.IsCanceled && !task.IsFaulted, "cancellation lost its token or canceled Task status");
            else
                Require(ReferenceEquals(observed, expected) && task.IsFaulted && !task.IsCanceled,
                    "fault lost its exception identity or faulted Task status");
            Require(source.ConsumptionCount == 1, "faulted source was not consumed exactly once");
            AssertLogicalCount(client, wrapper, 0);
        }
        finally
        {
            source.ReleaseForCleanup();
            await reader.DisposeAsync();
        }
        Require(source.DisposeCount == 1, "failed source disposal must occur once");
        AssertLogicalCount(client, wrapper, 0);
    }

    [Test]
    [Arguments("logical")]
    [Arguments("telemetry")]
    [Arguments("both")]
    public async Task CompletedUnconsumedResultSurvivesOtherWrapperReuse(string wrapper)
    {
        await using var oldClient = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        await using var newClient = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        var oldSource = new ReusableStream();
        var oldReader = Wrap(oldClient, oldSource, wrapper);
        var newSource = new ReusableStream();
        var newReader = Wrap(newClient, newSource, wrapper);
        var oldDisposed = false;
        try
        {
            var oldResult = oldReader.MoveNextAsync();
            Require(!oldResult.IsCompleted, "old result must come from a suspended wrapper");
            oldSource.Finish(false);
            Require(oldResult.IsCompletedSuccessfully, "old wrapper did not complete inline");
            // Dispose does not consume the outstanding result or return its builder box.
            await oldReader.DisposeAsync();
            oldDisposed = true;
            for (var iteration = 0; iteration < Warmups + Count; iteration++)
            {
                var next = newReader.MoveNextAsync();
                Require(!next.IsCompleted, "replacement must exercise the pooled wrapper");
                newSource.Finish(true);
                Require(next.GetAwaiter().GetResult(), "replacement lost its own item");
            }
            Require(!oldResult.GetAwaiter().GetResult(), "old unconsumed result observed a reused builder box");
            Require(oldSource.ConsumptionCount == 1 && oldSource.DisposeCount == 1,
                "old source ownership changed during another wrapper's reuse");
            Require(newSource.ConsumptionCount == Warmups + Count, "replacement consumed a source twice");
            AssertLogicalCount(oldClient, wrapper, 0);
            AssertLogicalCount(newClient, wrapper, 1);
        }
        finally
        {
            oldSource.ReleaseForCleanup();
            if (!oldDisposed) await oldReader.DisposeAsync();
            newSource.ReleaseForCleanup();
            await newReader.DisposeAsync();
        }
        AssertLogicalCount(newClient, wrapper, 0);
    }

    private static IAsyncEnumerator<string> Wrap(SharpLinkClient client, ReusableStream source, string wrapper)
    {
        IAsyncEnumerator<string> reader = source;
        if (wrapper is "telemetry" or "both")
        {
            var type = typeof(SharpLinkClient).GetNestedType("TelemetryAsyncEnumerator`1", BindingFlags.NonPublic)!
                .MakeGenericType(typeof(string));
            reader = (IAsyncEnumerator<string>)Activator.CreateInstance(type, Members, null,
                [reader, default(SharpLinkTelemetry.CallScope)], null)!;
        }
        if (wrapper is "logical" or "both")
        {
            typeof(SharpLinkClient).GetField("_activeLogicalInvocations", Members)!.SetValue(client, 1);
            var type = typeof(SharpLinkClient).GetNestedType("LogicalInvocationAsyncEnumerable`1", BindingFlags.NonPublic)!
                .MakeGenericType(typeof(string));
            var stream = (IAsyncEnumerable<string>)Activator.CreateInstance(type, Members, null,
                [client, new ExistingEnumerator(reader)], null)!;
            reader = stream.GetAsyncEnumerator();
        }
        return reader;
    }

    private static void AssertLogicalCount(SharpLinkClient client, string wrapper, int expected)
    {
        if (wrapper is "logical" or "both")
            Require(((ISharpLinkClientDrainInspector)client).ActiveCallCount == expected,
                "logical terminal/disposal accounting changed or completed twice");
    }

    private sealed class ExistingEnumerator(IAsyncEnumerator<string> reader) : IAsyncEnumerable<string>
    {
        public IAsyncEnumerator<string> GetAsyncEnumerator(CancellationToken cancellationToken = default) => reader;
    }

    private sealed class ReusableStream : IAsyncEnumerator<string>, IValueTaskSource<bool>
    {
        private ManualResetValueTaskSourceCore<bool> _core = new() { RunContinuationsAsynchronously = false };
        private int _state; // 0 = idle, 1 = pending, 2 = completed but not consumed.
        internal bool CompleteImmediately { get; set; }
        internal int ConsumptionCount { get; private set; }
        internal int DisposeCount { get; private set; }
        public string Current => "value";

        public ValueTask<bool> MoveNextAsync()
        {
            Require(_state == 0, "a new read began before the previous source was consumed");
            _state = 1;
            var result = new ValueTask<bool>(this, _core.Version);
            if (CompleteImmediately) Finish(true);
            return result;
        }

        internal void Finish(bool result)
        {
            Require(_state == 1, "read completed more than once");
            _state = 2;
            _core.SetResult(result);
        }

        internal void Fail(Exception error)
        {
            Require(_state == 1, "failed read was not pending");
            _state = 2;
            _core.SetException(error);
        }

        internal void ReleaseForCleanup() { if (_state == 1) Finish(false); }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }

        public bool GetResult(short token)
        {
            Require(_state == 2 && token == _core.Version, "source was consumed twice or with a stale version");
            try { return _core.GetResult(token); }
            finally
            {
                ConsumptionCount++;
                _core.Reset();
                _state = 0;
            }
        }

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _core.OnCompleted(continuation, state, token, flags);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
