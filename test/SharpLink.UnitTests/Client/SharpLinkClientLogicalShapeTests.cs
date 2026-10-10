using System.Reflection;
using System.Threading;
using SharpLink.Client;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Client;

[NotInParallel]
public sealed class SharpLinkClientLogicalShapeTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task SimpleUnaryShouldBalanceLogicalLifetimeOnSuccessRemoteErrorAndCancellation()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);
        await client.ConnectAsync();
        var inspector = (ISharpLinkClientDrainInspector)client;

        var success = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        var successRequest = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request)
            .WaitAsync(TestTimeout);
        Ensure(!success.IsCompleted && inspector.ActiveCallCount == 1,
            "suspended success must retain logical ownership");
        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)successRequest.RequestId), 123);
        Ensure(await success.WaitAsync(TestTimeout) == 123, "successful unary response");
        Ensure(inspector.ActiveCallCount == 0, "success must release once");

        var remoteError = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        var errorRequest = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request)
            .WaitAsync(TestTimeout);
        Ensure(!remoteError.IsCompleted && inspector.ActiveCallCount == 1,
            "suspended remote failure must retain logical ownership");
        await SharpLinkClientRetrySharedSupport.InjectErrorAsync(
            transport, errorRequest, SharpLinkErrorCode.Unavailable);
        var fault = await Throws<SharpLinkException>(remoteError.WaitAsync(TestTimeout));
        Ensure(fault.Code == SharpLinkErrorCode.Unavailable, "remote error must be preserved");
        Ensure(inspector.ActiveCallCount == 0, "remote error must release once");

        using var cancellation = new CancellationTokenSource();
        var cancelled = ClientInvokerTestHelper.InvokeUnaryAsync(
            client, cancellationToken: cancellation.Token).AsTask();
        _ = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request)
            .WaitAsync(TestTimeout);
        Ensure(!cancelled.IsCompleted && inspector.ActiveCallCount == 1,
            "suspended cancellation must retain logical ownership");
        cancellation.Cancel();
        _ = await Throws<OperationCanceledException>(cancelled.WaitAsync(TestTimeout));
        Ensure(inspector.ActiveCallCount == 0, "cancellation must release once");

        await client.StopAsync();
        Ensure(inspector.ActiveCallCount == 0, "later connection cleanup must not release again");
    }

    [Test]
    public async Task DrainInspectorMustNotReachZeroBeforeUnaryOperationBecomesTerminal()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);
        await client.ConnectAsync();
        var inspector = (ISharpLinkClientDrainInspector)client;

        // Seed the cached observer, then decorate it to pause precisely at the terminal hook.
        var first = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        var firstRequest = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request)
            .WaitAsync(TestTimeout);
        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)firstRequest.RequestId));
        _ = await first.WaitAsync(TestTimeout);

        var field = typeof(SharpLinkClient).GetField(
            "_logicalShapeObserver", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("cached shape observer field not found");
        var original = (IPendingCallCompletionObserver?)field.GetValue(client)
            ?? throw new InvalidOperationException("plain unary did not arm the shape observer");
        using var gate = new PausingPostOperationObserver(original);
        field.SetValue(client, gate);

        var invocation = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        var request = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request)
            .WaitAsync(TestTimeout);
        Ensure(!invocation.IsCompleted && inspector.ActiveCallCount == 1,
            "suspended invocation must be visible to graceful drain");

        // The RPC receive loop reaches the post-operation observer and blocks there.
        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)request.RequestId));
        await gate.Entered.WaitAsync(TestTimeout);
        Ensure(invocation.IsCompleted,
            "the operation must be terminal before the shape observer can release");
        Ensure(inspector.ActiveCallCount == 1,
            "graceful drain must not observe zero before the operation is terminal");

        gate.Release();
        await gate.Completed.WaitAsync(TestTimeout);
        Ensure(await invocation.WaitAsync(TestTimeout) == 0, "returned response");
        Ensure(inspector.ActiveCallCount == 0, "post-terminal hook must release once");
    }

    [Test]
    public async Task ThrowingDeadlineTimerAfterSlotPublicationMustNotDoubleRelease()
    {
        var provider = new ThrowingRegistrationTimeProvider();
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport, builder =>
            {
                builder.UseTimeProvider(provider);
                builder.UseRequestTimeout(TimeSpan.FromMinutes(5));
            });
        await client.ConnectAsync();
        var inspector = (ISharpLinkClientDrainInspector)client;

        provider.ThrowOnNextDeadlineArm();
        var invocation = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        var failure = await Throws<InvalidOperationException>(invocation.WaitAsync(TestTimeout));
        Ensure(failure.Message == "injected deadline timer failure",
            "the test must fail specifically after pending-slot publication");
        Ensure(provider.InjectedFailures == 1, "the pending deadline timer must have thrown once");
        Ensure(inspector.ActiveCallCount == 1,
            "the published pending call, not the failed Rent() caller, owns the logical release");

        await client.StopAsync();
        Ensure(inspector.ActiveCallCount == 0,
            "connection cleanup must finish the published call without logical underflow");
    }

    private static async Task<TException> Throws<TException>(Task task) where TException : Exception
    {
        try
        {
            await task;
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new Exception($"Expected {typeof(TException).Name}.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class PausingPostOperationObserver(IPendingCallCompletionObserver inner)
        : IPendingCallPostOperationObserver, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public Task Completed => _completed.Task;

        public void OnResponseObserved() => inner.OnResponseObserved();

        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        {
            _entered.TrySetResult();
            _release.Wait();
            inner.OnPendingCallCompleted(in completion);
            _completed.TrySetResult();
        }

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
        }
    }

    private sealed class ThrowingRegistrationTimeProvider : TimeProvider
    {
        private int _throwNextArm;
        private int _injectedFailures;

        public int InjectedFailures => Volatile.Read(ref _injectedFailures);

        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow();
        public override long GetTimestamp() => TimeProvider.System.GetTimestamp();

        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = TimeProvider.System.CreateTimer(callback, state, dueTime, period);
            return state is PendingDeadlineScheduler ? new ThrowingTimer(this, timer) : timer;
        }

        public void ThrowOnNextDeadlineArm() => Interlocked.Exchange(ref _throwNextArm, 1);

        private sealed class ThrowingTimer(ThrowingRegistrationTimeProvider owner, ITimer inner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (dueTime != Timeout.InfiniteTimeSpan &&
                    Interlocked.Exchange(ref owner._throwNextArm, 0) == 1)
                {
                    Interlocked.Increment(ref owner._injectedFailures);
                    throw new InvalidOperationException("injected deadline timer failure");
                }

                return inner.Change(dueTime, period);
            }

            public void Dispose() => inner.Dispose();
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
