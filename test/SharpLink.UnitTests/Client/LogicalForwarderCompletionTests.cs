using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

[NotInParallel]
public class LogicalForwarderCompletionTests
{
    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static ISharpLinkClient Client(IAsyncEnumerable<int> source)
        => SharpClientBuilder.Create().UseTransport(new NoTransport()).DisableRequestTimeout()
            .AddInterceptor(new SourceInterceptor(source)).Build();

    private static IAsyncEnumerable<int> Invoke(ISharpLinkClient client, CancellationToken cancellationToken = default, bool telemetry = false)
    {
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        var stream = channel.InvokeServerStreamingAsync(new RpcMethodDescriptor(1, 929, RpcMethodKind.ServerStreaming, true, false, false, null),
            request, RpcEmptyRequestCodec.Instance, channel.RuntimeContext.Codecs.GetCodec<int>(), null, cancellationToken);
        var optimizedProduct = typeof(SharpLinkClient).GetNestedType("CompletionOwnedLogicalInvocationAsyncEnumerable`1", BindingFlags.NonPublic) is not null;
        var expected = optimizedProduct && !telemetry ? "CompletionOwnedLogicalInvocationAsyncEnumerable" : "LogicalInvocationAsyncEnumerable";
        Ensure(stream.GetType().Name.StartsWith(expected, StringComparison.Ordinal),
            "Wrong public wrapper path: disable TUnit HTML reporter tracing and serialize this fixture; telemetry fallback is a separate case");
        return stream;
    }

    private static int Count(ISharpLinkClient client) => ((ISharpLinkClientDrainInspector)client).ActiveCallCount;

    private static object? Intercepted(object wrapper) => wrapper.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
        .Select(f => f.GetValue(wrapper)).FirstOrDefault(value => value?.GetType().Name.StartsWith("InterceptedAsyncEnumerable", StringComparison.Ordinal) == true);

    private static void Detached(object wrapper, bool expected)
    {
        var intercepted = Intercepted(wrapper);
        var field = intercepted?.GetType().GetField("_logicalCompletionOwner", BindingFlags.Instance | BindingFlags.NonPublic);
        // Baseline has no added edge. The same correctness tests run on both sources.
        if (field is not null) Ensure((field.GetValue(intercepted) is null) == expected, "logical owner edge lifetime");
    }

    private static async Task<Exception> Failure(ValueTask<bool> value)
    {
        try { _ = await value.ConfigureAwait(false); }
        catch (Exception exception) { return exception; }
        throw new InvalidOperationException("Expected move failure");
    }

    private static async Task<Exception> Failure(ValueTask value)
    {
        try { await value.ConfigureAwait(false); }
        catch (Exception exception) { return exception; }
        throw new InvalidOperationException("Expected dispose failure");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnstartedDisposeCompletesAndDetaches(bool acquire)
    {
        var source = new ScriptSource();
        await using var client = Client(source);
        var stream = Invoke(client);
        Ensure(Count(client) == 1, "invocation counted"); Detached(stream, false);
        var reader = acquire ? stream.GetAsyncEnumerator() : (IAsyncEnumerator<int>)stream;
        await reader.DisposeAsync(); await reader.DisposeAsync();
        Ensure(Count(client) == 0 && source.Acquisitions == 0, "unstarted disposal leaks or starts source");
        Detached(stream, true);
    }

    [Test]
    public async Task PendingTrueIsNotLogicalTerminalAndDuplicateAcquisitionDoesNotEndCall()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ScriptSource { Move = () => new ValueTask<bool>(gate.Task) };
        await using var client = Client(source); var stream = Invoke(client); var reader = stream.GetAsyncEnumerator();
        var move = reader.MoveNextAsync(); Ensure(!move.IsCompleted && Count(client) == 1, "pending item");
        try { stream.GetAsyncEnumerator(); throw new InvalidOperationException("duplicate accepted"); }
        catch (InvalidOperationException exception) when (exception.Message != "duplicate accepted") { }
        Ensure(Count(client) == 1, "duplicate ended first call");
        gate.SetResult(true); Ensure(await move, "true item");
        Ensure(Count(client) == 1, "yield counted as terminal"); Detached(stream, false);
        await reader.DisposeAsync(); Ensure(Count(client) == 0, "early disposal count"); Detached(stream, true);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnobservedTerminalCompletesAccountingAndPreservesResult(bool fault)
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new ApplicationException("move identity");
        var source = new ScriptSource { Move = () => new ValueTask<bool>(gate.Task) };
        await using var client = Client(source); var stream = Invoke(client); var reader = stream.GetAsyncEnumerator();
        var move = reader.MoveNextAsync();
        if (fault) gate.SetException(original); else gate.SetResult(false);
        await Eventually(() => move.IsCompleted && Count(client) == 0);
        Detached(stream, true);
        if (fault) Ensure(ReferenceEquals(await Failure(move), original), "original failure lost");
        else Ensure(!await move, "false terminal lost");
        await reader.DisposeAsync(); Ensure(Count(client) == 0, "double decrement");
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task PendingCleanupHoldsCountAndDisposalFailureWins(bool failMove, bool failDispose)
    {
        var disposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var moveFailure = new ApplicationException("source"); var disposeFailure = new InvalidOperationException("dispose");
        var source = new ScriptSource { Move = () => failMove ? ValueTask.FromException<bool>(moveFailure) : ValueTask.FromResult(false),
            Dispose = () => new ValueTask(disposal.Task) };
        await using var client = Client(source); var stream = Invoke(client); var reader = stream.GetAsyncEnumerator();
        var move = reader.MoveNextAsync();
        Ensure(source.Disposals == 1 && !move.IsCompleted && Count(client) == 1, "cleanup not retained"); Detached(stream, false);
        if (failDispose) disposal.SetException(disposeFailure); else disposal.SetResult();
        if (failMove || failDispose) Ensure(ReferenceEquals(await Failure(move), failDispose ? disposeFailure : moveFailure), "exception precedence/identity");
        else Ensure(!await move, "terminal false");
        Ensure(Count(client) == 0, "terminal count"); Detached(stream, true); await reader.DisposeAsync();
    }

    [Test]
    public async Task PendingExplicitDisposeHoldsCountThenDetaches()
    {
        var disposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ScriptSource { Move = () => ValueTask.FromResult(true), Dispose = () => new ValueTask(disposal.Task) };
        await using var client = Client(source); var stream = Invoke(client); var reader = stream.GetAsyncEnumerator();
        Ensure(await reader.MoveNextAsync(), "first"); var pending = reader.DisposeAsync();
        Ensure(!pending.IsCompleted && Count(client) == 1, "pending disposal count"); Detached(stream, false);
        disposal.SetResult(); await pending;
        Ensure(Count(client) == 0, "disposal terminal count"); Detached(stream, true);
    }

    [Test]
    public async Task SetupAndSynchronousWrapperFailureDetachWithoutLeakingCount()
    {
        var original = new ApplicationException("get-enumerator");
        var source = new ScriptSource { AcquisitionFailure = original };
        await using var client = Client(source); var stream = Invoke(client); var reader = stream.GetAsyncEnumerator();
        Ensure(ReferenceEquals(await Failure(reader.MoveNextAsync()), original), "setup identity");
        Ensure(Count(client) == 0, "setup count"); Detached(stream, true); await reader.DisposeAsync();
        var unacquired = Invoke(client);
        try { _ = ((IAsyncEnumerator<int>)unacquired).MoveNextAsync(); throw new ApplicationException("accepted"); }
        catch (InvalidOperationException) { }
        Ensure(Count(client) == 0, "synchronous wrapper failure count"); Detached(unacquired, true);
    }

    [Test]
    public async Task PublicWrapperPreservesCallerContextAndProducerFinallyOrder()
    {
        var ambient = new AsyncLocal<string?> { Value = "caller" };
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ISharpLinkClient? client = null;
        var source = new ScriptSource {
            OnAcquire = () => { ambient.Value = "acquire"; SynchronizationContext.SetSynchronizationContext(new SynchronizationContext()); },
            Move = () => { ambient.Value = "move"; return new ValueTask<bool>(gate.Task); },
            Dispose = () => { Ensure(Count(client!) == 1, "notified before user cleanup"); ambient.Value = "dispose"; return default; }
        };
        await using var owned = client = Client(source); var stream = Invoke(client); var reader = stream.GetAsyncEnumerator();
        var original = SynchronizationContext.Current; var context = new SynchronizationContext();
        ValueTask<bool> move;
        try {
            SynchronizationContext.SetSynchronizationContext(context); move = reader.MoveNextAsync();
            Ensure(ambient.Value == "caller" && ReferenceEquals(SynchronizationContext.Current, context), "producer context leaked");
        }
        finally { SynchronizationContext.SetSynchronizationContext(original); }
        gate.SetResult(false); Ensure(!await move.ConfigureAwait(false), "false");
        Ensure(ambient.Value == "caller" && Count(client) == 0, "terminal context/count"); Detached(stream, true);
        await reader.DisposeAsync();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReusableSourceIsConsumedOnceWithSupportedPublicConsumption(bool asTask)
    {
        var source = new RetainingSource(100);
        await using var client = Client(source); var stream = Invoke(client); var reader = stream.GetAsyncEnumerator();
        var count = 0;
        while (true)
        {
            var move = reader.MoveNextAsync();
            var next = asTask ? await move.AsTask() : await move;
            if (!next) break;
            Ensure(reader.Current == count++, "sequence");
        }
        await reader.DisposeAsync();
        Ensure(count == 100 && source.Results == 101 && source.Subscriptions == 101, "source consumed/subscribed more than once");
        Ensure(Count(client) == 0, "source accounting"); Detached(stream, true);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PublicFirstPassCancellationDoesNotEndBeforePendingCleanup(bool current)
    {
        using var cancellation = new CancellationTokenSource();
        var original = new OperationCanceledException("original source cancellation");
        var disposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int ThrowAndCancel() { try { throw original; } finally { cancellation.Cancel(); } }
        var source = new ScriptSource {
            Move = () => current ? ValueTask.FromResult(true) : ValueTask.FromResult(ThrowAndCancel() != 0),
            ReadCurrent = () => current ? ThrowAndCancel() : 0,
            Dispose = () => new ValueTask(disposal.Task)
        };
        await using var client = Client(source); var stream = Invoke(client, cancellation.Token); var reader = stream.GetAsyncEnumerator();
        var move = reader.MoveNextAsync();
        Ensure(source.Disposals == 1 && !move.IsCompleted && Count(client) == 1, "filter timing transferred ordinary failure prematurely");
        Detached(stream, false); disposal.SetResult();
        Ensure(ReferenceEquals(await Failure(move), original), "first-pass cancellation identity");
        Ensure(Count(client) == 0, "canceled logical terminal"); Detached(stream, true); await reader.DisposeAsync();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PendingInvocationSetupCompletesLogicalOwnerExactlyOnce(bool fault)
    {
        var invocation = new TaskCompletionSource<SharpLinkClientInvocationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new ApplicationException("invocation"); var source = new ScriptSource();
        await using var client = SharpClientBuilder.Create().UseTransport(new NoTransport()).DisableRequestTimeout()
            .AddInterceptor(new PendingInterceptor(invocation.Task)).Build();
        var stream = Invoke(client); var reader = stream.GetAsyncEnumerator(); var move = reader.MoveNextAsync();
        Ensure(!move.IsCompleted && Count(client) == 1 && source.Acquisitions == 0, "invocation wait ownership");
        if (fault) invocation.SetException(original); else invocation.SetResult(new SharpLinkClientInvocationResult(source));
        if (fault) Ensure(ReferenceEquals(await Failure(move), original), "invocation identity");
        else Ensure(!await move, "empty invocation stream");
        Ensure(Count(client) == 0, "invocation terminal count"); Detached(stream, true); await reader.DisposeAsync();
    }

    [Test]
    public async Task TelemetryOpaqueWrapperRetainsWorkingFallback()
    {
        var stopped = 0;
        using var listener = new ActivityListener {
            ShouldListenTo = static source => source.Name == "SharpLink.Client",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _ => Interlocked.Increment(ref stopped)
        };
        ActivitySource.AddActivityListener(listener);
        await using var client = Client(new ScriptSource()); var stream = Invoke(client, telemetry: true);
        Ensure(!stream.GetType().Name.StartsWith("CompletionOwned", StringComparison.Ordinal), "opaque telemetry incorrectly claimed capability");
        var reader = stream.GetAsyncEnumerator(); Ensure(!await reader.MoveNextAsync(), "telemetry empty result");
        await reader.DisposeAsync(); Ensure(Count(client) == 0 && stopped > 0, "telemetry/fallback completion lost");
    }

    [Test]
    public async Task CompiledIteratorDoesNotHoistAddedCompletionOwner()
    {
        await using var client = Client(new ScriptSource { Move = () => ValueTask.FromResult(true) });
        var stream = Invoke(client); var reader = stream.GetAsyncEnumerator(); Ensure(await reader.MoveNextAsync(), "first item");
        var inner = stream.GetType().GetField("_enumerator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(stream)!;
        var fields = inner.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Ensure(!fields.Any(f => f.FieldType.Name.StartsWith("CompletionOwnedLogicalInvocation", StringComparison.Ordinal)), "added owner hoisted into iterator");
        var beforeAttached = Intercepted(stream)?.GetType().GetField("_logicalCompletionOwner", BindingFlags.Instance | BindingFlags.NonPublic) is not null;
        await reader.DisposeAsync(); Detached(stream, true);
        Directory.CreateDirectory("artifacts");
        File.WriteAllText("artifacts/logical-capture-graph.json", JsonSerializer.Serialize(new {
            product = typeof(SharpClientBuilder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
            wrapperType = stream.GetType().ToString(), iteratorType = inner.GetType().ToString(), hasAddedEdge = beforeAttached,
            fields = fields.Select(f => new { f.Name, type = f.FieldType.ToString() }).ToArray(), terminalOwnerDetached = true
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task CompletedOperationAndRetainedSourceContinuationDoNotKeepAddedLogicalOwner(bool fault, bool pendingDispose)
    {
        var retained = await MakeRetentionRoot(fault, pendingDispose);
        for (var attempt = 0; attempt < 10 && retained.Owner.IsAlive; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Yield(); }
        Ensure(!retained.Owner.IsAlive, "completed iterator/source continuation retains logical wrapper through added edge");
        // No second consumption: merely retain the completed value and source record.
        GC.KeepAlive(retained.Operation); GC.KeepAlive(retained.Source);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<RetentionRoot> MakeRetentionRoot(bool fault, bool pendingDispose)
    {
        var failure = new ApplicationException("retained failure");
        var disposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new RetainingSource(pendingDispose ? 1 : 0) { Failure = fault ? failure : null,
            Disposal = pendingDispose ? () => new ValueTask(disposeGate.Task) : null };
        await using var client = Client(source); var stream = Invoke(client); var owner = new WeakReference(stream);
        var reader = stream.GetAsyncEnumerator(); var operation = reader.MoveNextAsync();
        if (fault) Ensure(ReferenceEquals(await Failure(operation), failure), "retained fault identity");
        else Ensure(await operation == pendingDispose, "retention result");
        var disposal = reader.DisposeAsync();
        if (pendingDispose) { Ensure(!disposal.IsCompleted, "retention pending dispose"); disposeGate.SetResult(); }
        await disposal; Detached(stream, true);
        return new RetentionRoot(operation, source, owner);
    }

    private sealed record RetentionRoot(ValueTask<bool> Operation, RetainingSource Source, WeakReference Owner);

    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Delay(1, timeout.Token); }
    }

    private sealed class ScriptSource : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        public Func<ValueTask<bool>> Move = () => ValueTask.FromResult(false);
        public Func<ValueTask> Dispose = () => default;
        public Action? OnAcquire;
        public Exception? AcquisitionFailure;
        public int Acquisitions, Disposals;
        public Func<int> ReadCurrent = () => 0;
        public int Current => ReadCurrent();
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        { Acquisitions++; OnAcquire?.Invoke(); if (AcquisitionFailure is not null) throw AcquisitionFailure; return this; }
        public ValueTask<bool> MoveNextAsync() => Move();
        public ValueTask DisposeAsync() { Disposals++; return Dispose(); }
    }

    private sealed class RetainingSource(int items) : IAsyncEnumerable<int>, IAsyncEnumerator<int>, IValueTaskSource<bool>, IThreadPoolWorkItem
    {
        private ManualResetValueTaskSourceCore<bool> _promise = new() { RunContinuationsAsynchronously = true };
        private int _index;
        public int Results, Subscriptions;
        public Exception? Failure;
        public Func<ValueTask>? Disposal;
        public Action<object?>? RetainedContinuation;
        public object? RetainedState;
        public int Current => _index - 1;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() { _promise.Reset(); return new ValueTask<bool>(this, _promise.Version); }
        public ValueTask DisposeAsync() => Disposal?.Invoke() ?? default;
        public void Execute() { if (Failure is not null) _promise.SetException(Failure); else _promise.SetResult(_index++ < items); }
        public bool GetResult(short token) { Results++; return _promise.GetResult(token); }
        public ValueTaskSourceStatus GetStatus(short token) => _promise.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            Subscriptions++; RetainedContinuation = continuation; RetainedState = state;
            _promise.OnCompleted(continuation, state, token, flags); ThreadPool.UnsafeQueueUserWorkItem(this, false);
        }
    }

    private sealed class PendingInterceptor(Task<SharpLinkClientInvocationResult> pending) : ISharpLinkClientInterceptor
    {
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(SharpLinkClientInvocationContext context, SharpLinkClientInvocationDelegate next) => new(pending);
    }
    private sealed class SourceInterceptor(IAsyncEnumerable<int> source) : ISharpLinkClientInterceptor
    {
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(SharpLinkClientInvocationContext context, SharpLinkClientInvocationDelegate next)
            => ValueTask.FromResult(new SharpLinkClientInvocationResult(source));
    }
    private sealed class NoTransport : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("No network expected");
        public ValueTask DisposeAsync() => default;
    }
}
