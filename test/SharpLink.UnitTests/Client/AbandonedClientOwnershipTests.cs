using System.Reflection;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public class AbandonedClientOwnershipTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Test]
    [Arguments(RpcMethodKind.Unary, false)]
    [Arguments(RpcMethodKind.OneWay, false)]
    [Arguments(RpcMethodKind.ClientStreaming, false)]
    [Arguments(RpcMethodKind.ServerStreaming, false)]
    [Arguments(RpcMethodKind.DuplexStreaming, false)]
    [Arguments(RpcMethodKind.Unary, true)]
    [Arguments(RpcMethodKind.OneWay, true)]
    [Arguments(RpcMethodKind.ClientStreaming, true)]
    [Arguments(RpcMethodKind.ServerStreaming, true)]
    [Arguments(RpcMethodKind.DuplexStreaming, true)]
    public async Task DeadlineRetainsModuleUntilInterceptorPhysicallyCompletes(RpcMethodKind kind, bool lateFault)
    {
        var interceptor = new DelayedInterceptor(lateFault);
        await using var fixture = new Fixture(interceptor);
        try
        {
            var call = Invoke(fixture.Channel, kind);
            await interceptor.Entered.Task.WaitAsync(TestTimeout);
            fixture.Clock.Advance(TimeSpan.FromSeconds(5));
            var failure = await CaptureFailure(call);
            Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded }, "public deadline");
            fixture.Module.TryBeginDraining();
            Ensure(fixture.Module.RemainingCalls > 0, "the physical interceptor must still own its module");
            Ensure(!fixture.Module.WaitForDrainAsync().IsCompleted, "logical deadline is not physical completion");
            interceptor.Release.TrySetResult();
            await fixture.Module.WaitForDrainAsync().WaitAsync(TestTimeout);
            Ensure(fixture.Module.RemainingCalls == 0, "physical completion releases all parent/producer leases");
            fixture.Module.AssertAccountingInvariant();
        }
        finally { interceptor.Release.TrySetResult(); }
    }

    [Test]
    [Arguments(RpcMethodKind.Unary)]
    [Arguments(RpcMethodKind.OneWay)]
    [Arguments(RpcMethodKind.ClientStreaming)]
    [Arguments(RpcMethodKind.ServerStreaming)]
    [Arguments(RpcMethodKind.DuplexStreaming)]
    public async Task DeadlinePreservesCallerAndModuleCancellationLink(RpcMethodKind kind)
    {
        var interceptor = new CancellationInterceptor();
        using var caller = new CancellationTokenSource();
        await using var fixture = new Fixture(interceptor);
        try
        {
            var call = Invoke(fixture.Channel, kind, caller.Token);
            await interceptor.Entered.Task.WaitAsync(TestTimeout);
            fixture.Clock.Advance(TimeSpan.FromSeconds(5));
            _ = await CaptureFailure(call);
            fixture.Module.TryBeginDraining();
            fixture.Module.CancelRemainingCalls();
            await interceptor.Cancelled.Task.WaitAsync(TestTimeout);
            await fixture.Module.WaitForDrainAsync().WaitAsync(TestTimeout);
            Ensure(!caller.IsCancellationRequested, "module cancellation must not cancel the caller source");
        }
        finally { interceptor.Release.TrySetResult(); }
    }

    [Test]
    [Arguments(RpcMethodKind.ServerStreaming, false, false)]
    [Arguments(RpcMethodKind.DuplexStreaming, false, false)]
    [Arguments(RpcMethodKind.ServerStreaming, true, false)]
    [Arguments(RpcMethodKind.DuplexStreaming, true, false)]
    [Arguments(RpcMethodKind.ServerStreaming, false, true)]
    [Arguments(RpcMethodKind.DuplexStreaming, false, true)]
    public async Task StreamTransfersSequentialCleanupBeforePublicTerminal(
        RpcMethodKind kind, bool cancelWaiter, bool lateFault)
    {
        var stream = new ControlledStream();
        using var caller = new CancellationTokenSource();
        await using var fixture = new Fixture(new StreamInterceptor(stream));
        await LongRunningTestWorker.Run(() =>
        {
            Task? call = null;
            Task? cancellation = null;
            try
            {
                call = Invoke(fixture.Channel, kind, caller.Token);
                LongRunningTestWorker.WaitForPhase(stream.Entered.Task, TestTimeout);
                if (cancelWaiter)
                    cancellation = LongRunningTestWorker.Run(() => caller.Cancel());
                else
                    fixture.Clock.Advance(TimeSpan.FromSeconds(5));
                var failure = CaptureFailureSynchronously(call);
                Ensure(cancelWaiter ? failure is OperationCanceledException :
                    failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded }, "selected public terminal");
                fixture.Module.TryBeginDraining();
                Ensure(stream.DisposeCount == 0, "Dispose must not run concurrently with abandoned MoveNext");
                Ensure(!fixture.Module.WaitForDrainAsync().IsCompleted, "pending MoveNext retains module");
                if (lateFault)
                    stream.Move.TrySetException(new InvalidOperationException("late MoveNext failure"));
                else
                    stream.Move.TrySetResult(false);
                LongRunningTestWorker.WaitForPhase(stream.DisposeEntered.Task, TestTimeout);
                Ensure(!fixture.Module.WaitForDrainAsync().IsCompleted, "pending synchronous Dispose retains module");
                stream.DisposeRelease.Set();
                LongRunningTestWorker.WaitForPhase(fixture.Module.WaitForDrainAsync(), TestTimeout);
                Ensure(stream.DisposeCount == 1, "Dispose is invoked exactly once");
                Ensure(!stream.ConcurrentDispose, "MoveNext completes before Dispose entry");
            }
            finally
            {
                stream.Move.TrySetResult(false);
                stream.DisposeRelease.Set();
                if (cancellation is not null)
                    LongRunningTestWorker.Join(cancellation, TestTimeout);
                if (call is not null)
                    LongRunningTestWorker.Join(call, TestTimeout);
                fixture.Module.TryBeginDraining();
                LongRunningTestWorker.WaitForPhase(fixture.Module.WaitForDrainAsync(), TestTimeout);
            }
        });
    }

    [Test]
    public async Task DeadlineDoesNotRunBlockingCancellationCallbackInline()
    {
        var stream = new ControlledStream(blockCancellation: true);
        await using var fixture = new Fixture(new StreamInterceptor(stream));
        await LongRunningTestWorker.Run(() =>
        {
            Task? call = null;
            try
            {
                call = Invoke(fixture.Channel, RpcMethodKind.ServerStreaming);
                LongRunningTestWorker.WaitForPhase(stream.Entered.Task, TestTimeout);
                fixture.Clock.Advance(TimeSpan.FromSeconds(5));
                var failure = CaptureFailureSynchronously(call);
                Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded }, "deadline precedes blocking callback");
                LongRunningTestWorker.WaitForPhase(stream.CancellationEntered.Task, TestTimeout);
                fixture.Module.TryBeginDraining();
                Ensure(!fixture.Module.WaitForDrainAsync().IsCompleted, "cancellation callback retains physical owner");
                stream.CancellationRelease.Set();
                stream.Move.TrySetResult(false);
                stream.DisposeRelease.Set();
                LongRunningTestWorker.WaitForPhase(fixture.Module.WaitForDrainAsync(), TestTimeout);
            }
            finally
            {
                stream.CancellationRelease.Set();
                stream.Move.TrySetResult(false);
                stream.DisposeRelease.Set();
                if (call is not null)
                    LongRunningTestWorker.Join(call, TestTimeout);
                fixture.Module.TryBeginDraining();
                LongRunningTestWorker.WaitForPhase(fixture.Module.WaitForDrainAsync(), TestTimeout);
            }
        });
    }

    [Test]
    [Arguments(RpcMethodKind.ServerStreaming)]
    [Arguments(RpcMethodKind.DuplexStreaming)]
    public async Task UntimedCooperativeCancellationReturnsBeforePhysicalDisposal(RpcMethodKind kind)
    {
        var stream = new ControlledStream(cooperativeCancellation: true);
        using var caller = new CancellationTokenSource();
        await using var fixture = new Fixture(new StreamInterceptor(stream));
        await LongRunningTestWorker.Run(() =>
        {
            Task? call = null;
            Task? cancellation = null;
            try
            {
                call = Invoke(fixture.Channel, kind, caller.Token, timed: false);
                LongRunningTestWorker.WaitForPhase(stream.Entered.Task, TestTimeout);
                cancellation = LongRunningTestWorker.Run(() => caller.Cancel());
                Ensure(CaptureFailureSynchronously(call) is OperationCanceledException, "untimed cooperative cancellation");
                LongRunningTestWorker.WaitForPhase(stream.DisposeEntered.Task, TestTimeout);
                fixture.Module.TryBeginDraining();
                Ensure(!fixture.Module.WaitForDrainAsync().IsCompleted, "Dispose owns the module after public cancellation");
                stream.DisposeRelease.Set();
                LongRunningTestWorker.WaitForPhase(fixture.Module.WaitForDrainAsync(), TestTimeout);
            }
            finally
            {
                stream.Move.TrySetResult(false);
                stream.DisposeRelease.Set();
                if (cancellation is not null)
                    LongRunningTestWorker.Join(cancellation, TestTimeout);
                if (call is not null)
                    LongRunningTestWorker.Join(call, TestTimeout);
                fixture.Module.TryBeginDraining();
                LongRunningTestWorker.WaitForPhase(fixture.Module.WaitForDrainAsync(), TestTimeout);
            }
        });
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task ReusedPublicTerminalMustNotMakeCleanupWaitForItself(bool throwFromDispose, bool useAlias)
    {
        var stream = new ControlledStream();
        await using var fixture = new Fixture(new StreamInterceptor(stream));
        await LongRunningTestWorker.Run(() =>
        {
            Task? call = null;
            try
            {
                call = Invoke(fixture.Channel, RpcMethodKind.ServerStreaming);
                LongRunningTestWorker.WaitForPhase(stream.Entered.Task, TestTimeout);
                fixture.Clock.Advance(TimeSpan.FromSeconds(5));
                var terminal = CaptureFailureSynchronously(call);
                if (useAlias)
                {
                    var replacement = new InvalidOperationException("replacement terminal");
                    typeof(SharpLinkRuntimeContext).Assembly.GetType("SharpLink.Runtime.SharpLinkPhysicalWork")?
                        .GetMethod("PreserveAbandonedUserWork", BindingFlags.Static | BindingFlags.NonPublic)?
                        .Invoke(null, [terminal, replacement]);
                    terminal = replacement;
                }
                fixture.Module.TryBeginDraining();
                if (throwFromDispose)
                {
                    stream.DisposeException = terminal;
                    stream.Move.TrySetResult(false);
                }
                else
                    stream.Move.TrySetException(terminal);
                stream.DisposeRelease.Set();
                LongRunningTestWorker.WaitForPhase(fixture.Module.WaitForDrainAsync(), TestTimeout);
            }
            finally
            {
                stream.Move.TrySetResult(false);
                stream.DisposeRelease.Set();
                if (call is not null)
                    LongRunningTestWorker.Join(call, TestTimeout);
                fixture.Module.TryBeginDraining();
                LongRunningTestWorker.WaitForPhase(fixture.Module.WaitForDrainAsync(), TestTimeout);
            }
        });
    }

    [Test]
    public async Task RecycledCrossCallTerminalsRetainBothModulesUntilBothDisposalsFinish()
    {
        var a = new ControlledStream();
        var b = new ControlledStream();
        var firstInterceptor = new StreamInterceptor(a);
        var secondInterceptor = new StreamInterceptor(b);
        using var firstCaller = new CancellationTokenSource();
        using var secondCaller = new CancellationTokenSource();
        await using var first = new Fixture(firstInterceptor);
        await using var second = new Fixture(secondInterceptor);
        await LongRunningTestWorker.Run(() =>
        {
            Task? firstCall = null;
            Task? secondCall = null;
            try
            {
                firstCall = Invoke(first.Channel, RpcMethodKind.ServerStreaming, firstCaller.Token);
                secondCall = Invoke(second.Channel, RpcMethodKind.ServerStreaming, secondCaller.Token);
                LongRunningTestWorker.WaitForPhase(Task.WhenAll(a.Entered.Task, b.Entered.Task), TestTimeout);
                first.Clock.Advance(TimeSpan.FromSeconds(5));
                second.Clock.Advance(TimeSpan.FromSeconds(5));
                var firstTerminal = CaptureFailureSynchronously(firstCall);
                var secondTerminal = CaptureFailureSynchronously(secondCall);
                first.Module.TryBeginDraining();
                second.Module.TryBeginDraining();
                a.Move.TrySetException(secondTerminal);
                b.Move.TrySetException(firstTerminal);
                // Do not require two pool workers to be blocked simultaneously. Whichever
                // real synchronous disposer starts first can finish while its peer stays held.
                // All entry/handoff observations share the original five-second phase budget.
                var entryStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                var entered = Task.WaitAny([a.DisposeEntered.Task, b.DisposeEntered.Task], TestTimeout);
                Ensure(entered >= 0, "one component disposer must enter within the phase budget");
                var completedFirst = entered == 0 ? a : b;
                var held = entered == 0 ? b : a;
                completedFirst.DisposeRelease.Set();
                LongRunningTestWorker.WaitForPhase(completedFirst.DisposeFinished.Task, RemainingEntryBudget());
                LongRunningTestWorker.WaitForPhase(held.DisposeEntered.Task, RemainingEntryBudget());
                Ensure(!first.Module.WaitForDrainAsync().IsCompleted && !second.Module.WaitForDrainAsync().IsCompleted,
                    "both component modules must stay retained while either disposal is pending");
                Ensure(IsAlive(a.Token) && IsAlive(b.Token) && IsAlive(firstInterceptor.Token) && IsAlive(secondInterceptor.Token),
                    "both inner and outer linked sources stay retained until all component cleanup finishes");
                held.DisposeRelease.Set();
                LongRunningTestWorker.WaitForPhase(Task.WhenAll(first.Module.WaitForDrainAsync(), second.Module.WaitForDrainAsync()), TestTimeout);
                Ensure(!a.ConcurrentDispose && !b.ConcurrentDispose, "every local MoveNext precedes its own Dispose");

                TimeSpan RemainingEntryBudget()
                {
                    var remaining = TestTimeout - System.Diagnostics.Stopwatch.GetElapsedTime(entryStarted);
                    if (remaining <= TimeSpan.Zero)
                        throw new TimeoutException("The component disposal entry phase timed out.");
                    return remaining;
                }
            }
            finally
            {
                a.Move.TrySetResult(false); b.Move.TrySetResult(false);
                a.DisposeRelease.Set(); b.DisposeRelease.Set();
                if (firstCall is not null) LongRunningTestWorker.Join(firstCall, TestTimeout);
                if (secondCall is not null) LongRunningTestWorker.Join(secondCall, TestTimeout);
                first.Module.TryBeginDraining(); second.Module.TryBeginDraining();
                LongRunningTestWorker.WaitForPhase(Task.WhenAll(first.Module.WaitForDrainAsync(), second.Module.WaitForDrainAsync()), TestTimeout);
            }

            static bool IsAlive(CancellationToken token)
            {
                try { _ = token.WaitHandle; return true; }
                catch (ObjectDisposedException) { return false; }
            }
        });
    }

    internal static async Task Invoke(
        IRpcChannel channel, RpcMethodKind kind, CancellationToken token = default, bool timed = true,
        IRpcCodec<RpcEmptyRequest>? requestCodec = null)
    {
        var method = new RpcMethodDescriptor(1, 929, kind, kind != RpcMethodKind.OneWay,
            kind is RpcMethodKind.ClientStreaming or RpcMethodKind.DuplexStreaming, timed,
            timed ? TimeSpan.FromSeconds(5) : null);
        var request = default(RpcEmptyRequest);
        requestCodec ??= RpcEmptyRequestCodec.Instance;
        var codec = channel.RuntimeContext.Codecs.GetCodec<int>();
        switch (kind)
        {
            case RpcMethodKind.Unary:
                _ = await channel.InvokeUnaryAsync(method, request, requestCodec, codec, null, token);
                break;
            case RpcMethodKind.OneWay:
                await channel.InvokeOneWayAsync(method, request, requestCodec, new EmptyStreams(), null, token);
                break;
            case RpcMethodKind.ClientStreaming:
                _ = await channel.InvokeClientStreamingAsync(method, request, requestCodec, codec, new EmptyStreams(), null, token);
                break;
            default:
                var stream = kind == RpcMethodKind.ServerStreaming
                    ? channel.InvokeServerStreamingAsync(method, request, requestCodec, codec, null, token)
                    : channel.InvokeDuplexStreamingAsync(method, request, requestCodec, codec, new EmptyStreams(), null, token);
                await using (var enumerator = stream.GetAsyncEnumerator())
                    _ = await enumerator.MoveNextAsync();
                break;
        }
    }

    private static Exception CaptureFailureSynchronously(Task task)
    {
        try { LongRunningTestWorker.WaitForPhase(task, TestTimeout); }
        catch (Exception exception) when (exception is not TimeoutException) { return exception; }
        throw new Exception("Expected the selected public terminal.");
    }

    private static async Task<Exception> CaptureFailure(Task task)
    {
        try { await task.WaitAsync(TestTimeout); }
        catch (Exception exception) when (exception is not TimeoutException) { return exception; }
        throw new Exception("expected invocation failure");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly SharpLinkRuntimeContext _moduleContext;
        private readonly RpcGeneratedManifestRegistration _registration;
        internal Fixture(ISharpLinkClientInterceptor interceptor)
        {
            Client = ClientBuilderTestHelper.Build(new TestClientTransportFactory(), builder =>
            {
                builder.UseTimeProvider(Clock);
                builder.AddInterceptor(interceptor);
            });
            _moduleContext = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
            var manifest = new EmptyManifest();
            _registration = _moduleContext.PrepareGeneratedManifest(manifest);
            Module = new SharpLinkDynamicModule(manifest.OwnerAssembly, manifest, _registration);
            Channel = new SharpLinkModuleRpcChannel(Client, Module);
        }
        internal ManualTimeProvider Clock { get; } = new();
        internal SharpLinkClient Client { get; }
        internal SharpLinkDynamicModule Module { get; }
        internal IRpcChannel Channel { get; }
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            _registration.Dispose();
            _moduleContext.Dispose();
        }
    }

    private sealed class DelayedInterceptor(bool lateFault) : ISharpLinkClientInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context, SharpLinkClientInvocationDelegate next)
        {
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            if (lateFault)
                throw new InvalidOperationException("late interceptor failure");
            return Result(context.Method.Kind);
        }
    }

    private sealed class CancellationInterceptor : ISharpLinkClientInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context, SharpLinkClientInvocationDelegate next)
        {
            Entered.TrySetResult();
            using var registration = context.CancellationToken.Register(() => Cancelled.TrySetResult());
            await Task.WhenAny(Cancelled.Task, Release.Task).ConfigureAwait(false);
            return Result(context.Method.Kind);
        }
    }

    private static SharpLinkClientInvocationResult Result(RpcMethodKind kind)
        => kind switch
        {
            RpcMethodKind.OneWay => default,
            RpcMethodKind.ServerStreaming or RpcMethodKind.DuplexStreaming => new(new EmptyStream()),
            _ => new(42)
        };

    private sealed class StreamInterceptor(IAsyncEnumerable<int> stream) : ISharpLinkClientInterceptor
    {
        internal CancellationToken Token;
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context, SharpLinkClientInvocationDelegate next)
        {
            Token = context.CancellationToken;
            return ValueTask.FromResult(new SharpLinkClientInvocationResult(stream));
        }
    }

    private sealed class ControlledStream(bool blockCancellation = false, bool cooperativeCancellation = false) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Move { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancellationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim DisposeRelease { get; } = new(false);
        internal ManualResetEventSlim CancellationRelease { get; } = new(false);
        private CancellationTokenRegistration _registration;
        internal int DisposeCount;
        internal bool ConcurrentDispose;
        internal Exception? DisposeException;
        internal CancellationToken Token;
        public int Current => 42;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            if (cooperativeCancellation)
                _registration = cancellationToken.Register(() => Move.TrySetCanceled(cancellationToken));
            else if (blockCancellation)
                _registration = cancellationToken.Register(() =>
                {
                    CancellationEntered.TrySetResult();
                    CancellationRelease.Wait();
                });
            return this;
        }
        public ValueTask<bool> MoveNextAsync()
        {
            Entered.TrySetResult();
            return new ValueTask<bool>(Move.Task);
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCount);
            ConcurrentDispose = !Move.Task.IsCompleted;
            DisposeEntered.TrySetResult();
            DisposeRelease.Wait();
            _registration.Dispose();
            if (DisposeException is { } failure)
                throw failure;
            DisposeFinished.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EmptyStream : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        public int Current => 42;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private readonly struct EmptyStreams : IRpcClientStreamWriter
    {
        public ValueTask WriteAsync(IRpcClientStreamSink sink, long requestId, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private sealed class EmptyManifest : ITestGeneratedManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(AbandonedClientOwnershipTests).Assembly;
        public string CompileTimeDescriptor => "test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }
}
