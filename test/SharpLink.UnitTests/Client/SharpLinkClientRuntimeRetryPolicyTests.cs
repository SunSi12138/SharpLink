using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;
using static SharpLink.UnitTests.Client.SharpLinkClientRetrySharedSupport;

namespace SharpLink.UnitTests.Client;

[NotInParallel]
public sealed class SharpLinkClientRuntimeRetryPolicyTests
{
    [Test]
    public async Task BuiltInUpdateShouldOnlyAffectFutureLogicalCalls()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport, builder =>
            ConfigureRetry(builder, RetryOptions(2, TimeSpan.Zero)));
        await client.ConnectAsync();

        var oldGenerationCall = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(client).AsTask();
        var oldFirst = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);

        client.UpdateRetryPolicy(RetryOptions(1, TimeSpan.Zero));
        await InjectErrorAsync(transport, oldFirst, SharpLinkErrorCode.Unavailable);
        var oldSecond = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)oldSecond.RequestId));
        Ensure(await oldGenerationCall == 0,
            "in-flight logical call must retain the captured two-attempt generation");

        var newGenerationCall = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(client).AsTask();
        var newFirst = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await InjectErrorAsync(transport, newFirst, SharpLinkErrorCode.Unavailable);
        var failure = await EnsureThrows<SharpLinkException>(newGenerationCall);
        Ensure(failure.Code == SharpLinkErrorCode.Unavailable,
            "later logical call should expose the first failure under max-attempts one");
        Ensure(!await transport.Connection.TryWaitForSentPacket(
            ProtocolV2FrameType.Request,
            TimeSpan.FromMilliseconds(100)),
            "later logical call must not retry under the new generation");
    }

    [Test]
    public async Task InterceptorSuspensionShouldRetainCapturedRetryGeneration()
    {
        var transport = new TestClientTransportFactory();
        var interceptor = new OneShotSuspendingInterceptor();
        await using var client = ClientBuilderTestHelper.Build(transport, builder =>
        {
            ConfigureRetry(builder, RetryOptions(2, TimeSpan.Zero));
            builder.AddInterceptor(interceptor);
        });
        await client.ConnectAsync();

        var invocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(client).AsTask();
        await interceptor.Entered.WaitAsync(TimeSpan.FromSeconds(2));

        client.DisableRetry();
        interceptor.Release();

        var first = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await InjectErrorAsync(transport, first, SharpLinkErrorCode.Unavailable);
        var second = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)second.RequestId));
        Ensure(await invocation == 0,
            "logical call suspended inside an interceptor must retain its pre-disable retry generation");

        var later = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(client).AsTask();
        var laterFirst = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await InjectErrorAsync(transport, laterFirst, SharpLinkErrorCode.Unavailable);
        _ = await EnsureThrows<SharpLinkException>(later);
        Ensure(!await transport.Connection.TryWaitForSentPacket(
            ProtocolV2FrameType.Request,
            TimeSpan.FromMilliseconds(100)),
            "future logical call should observe disabled retries");
    }

    [Test]
    public async Task UpdateBetweenAttemptsShouldNotMutateCurrentCustomLoop()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        var policy = new RecordingDelayPolicy(TimeSpan.FromSeconds(1));
        await using var client = ClientBuilderTestHelper.Build(transport, builder =>
        {
            builder.UseTimeProvider(timeProvider);
            builder.DisableRequestTimeout();
        });
        client.UpdateRetryPolicy(policy, RetryOptions(2, TimeSpan.Zero));
        await client.ConnectAsync();

        var invocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(client).AsTask();
        var first = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await InjectErrorAsync(transport, first, SharpLinkErrorCode.Unavailable);
        await policy.EvaluationStarted.WaitAsync(TimeSpan.FromSeconds(2));

        client.DisableRetry();
        timeProvider.Advance(TimeSpan.FromSeconds(1));

        var second = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)second.RequestId));
        Ensure(await invocation == 0,
            "policy update between attempts must not alter the captured retry loop");
        Ensure(policy.Count == 1, "captured custom policy should evaluate exactly once");

        var later = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(client).AsTask();
        var laterFirst = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await InjectErrorAsync(transport, laterFirst, SharpLinkErrorCode.Unavailable);
        _ = await EnsureThrows<SharpLinkException>(later);
        Ensure(!await transport.Connection.TryWaitForSentPacket(
            ProtocolV2FrameType.Request,
            TimeSpan.FromMilliseconds(100)),
            "future logical call should observe the disabled generation");
    }

    [Test]
    public async Task BuiltInCustomAndDisabledTransitionsShouldPublishCompleteGenerations()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);

        var initial = client.GetRetryPolicySnapshot();
        Ensure(initial.Generation == 0 && !initial.Enabled &&
               initial.Kind == SharpLinkRetryPolicyKind.Disabled,
            "builder default should expose disabled generation zero");

        var options = RetryOptions(4, TimeSpan.FromMilliseconds(10));
        client.UpdateRetryPolicy(options);
        var builtIn = client.GetRetryPolicySnapshot();
        Ensure(builtIn.Generation == 1 && builtIn.Kind == SharpLinkRetryPolicyKind.BuiltIn &&
               builtIn.MaxAttempts == 4 && builtIn.InitialBackoff == TimeSpan.FromMilliseconds(10),
            "built-in update should publish one complete copied generation");

        options.MaxAttempts = 1;
        Ensure(client.GetRetryPolicySnapshot() == builtIn,
            "mutating the caller options object after publication must not alter the generation");

        var policy = new AlwaysRetryPolicy();
        client.UpdateRetryPolicy(policy, RetryOptions(2, TimeSpan.Zero));
        var custom = client.GetRetryPolicySnapshot();
        Ensure(custom.Generation == 2 && custom.Kind == SharpLinkRetryPolicyKind.Custom &&
               custom.MaxAttempts == 2,
            "custom transition should atomically publish custom strategy and attempt bounds");

        var invalid = new SharpLinkRetryOptions
        {
            MaxAttempts = 0,
            InitialBackoff = TimeSpan.Zero,
            MaxBackoff = TimeSpan.Zero,
            JitterRatio = 0
        };
        Ensure(CaptureException(() => client.UpdateRetryPolicy(invalid)) is ArgumentOutOfRangeException,
            "invalid built-in candidate should be rejected before publication");
        Ensure(client.GetRetryPolicySnapshot() == custom,
            "invalid candidate must leave the current custom generation unchanged");

        client.DisableRetry();
        var disabled = client.GetRetryPolicySnapshot();
        Ensure(disabled.Generation == 3 && disabled.Kind == SharpLinkRetryPolicyKind.Disabled &&
               !disabled.Enabled,
            "disable should publish one complete disabled generation");
        client.DisableRetry();
        Ensure(client.GetRetryPolicySnapshot() == disabled,
            "idempotent disable should not manufacture another generation");
    }

    [Test]
    public async Task RuntimeCustomPolicyFailureShouldKeepExistingFailureMappingAndCleanup()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);
        client.UpdateRetryPolicy(new ThrowingRetryPolicy(), RetryOptions(2, TimeSpan.Zero));
        await client.ConnectAsync();

        var invocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(client).AsTask();
        var first = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await InjectErrorAsync(transport, first, SharpLinkErrorCode.Unavailable);

        var failure = await EnsureThrows<SharpLinkException>(invocation);
        Ensure(failure.Code == SharpLinkErrorCode.FailedPrecondition,
            "runtime custom policy exception should retain FailedPrecondition mapping");
        Ensure(((ISharpLinkClientDrainInspector)client).ActiveCallCount == 0,
            "custom policy failure must release the logical invocation");
        Ensure(client.PendingCallCount == 0 && client.ActiveClientCallCount == 0,
            "custom policy failure must release pending and attempt state");
    }

    [Test]
    public async Task ConcurrentTransitionsAndStopShouldNotPublishTornOrLateGenerations()
    {
        var transport = new TestClientTransportFactory();
        var client = ClientBuilderTestHelper.Build(transport);
        try
        {
            var custom = new AlwaysRetryPolicy();
            Parallel.For(0, 96, index =>
            {
                switch (index % 3)
                {
                    case 0:
                        client.UpdateRetryPolicy(RetryOptions(2 + index % 4, TimeSpan.Zero));
                        break;
                    case 1:
                        client.UpdateRetryPolicy(custom, RetryOptions(2, TimeSpan.Zero));
                        break;
                    default:
                        client.DisableRetry();
                        break;
                }

                var snapshot = client.GetRetryPolicySnapshot();
                Ensure(snapshot.Kind switch
                {
                    SharpLinkRetryPolicyKind.Disabled =>
                        !snapshot.Enabled && snapshot.MaxAttempts == 0,
                    SharpLinkRetryPolicyKind.BuiltIn or SharpLinkRetryPolicyKind.Custom =>
                        snapshot.Enabled && snapshot.MaxAttempts is >= 1 and <= 10,
                    _ => false
                }, "concurrent snapshot must expose one complete generation");
            });

            await client.StopAsync();
            Ensure(CaptureException(() => client.UpdateRetryPolicy(RetryOptions(2, TimeSpan.Zero)))
                   is InvalidOperationException,
                "built-in update must be rejected after stop begins");
            Ensure(CaptureException(() => client.UpdateRetryPolicy(custom)) is InvalidOperationException,
                "custom update must be rejected after stop begins");
            Ensure(CaptureException(client.DisableRetry) is InvalidOperationException,
                "disable must be rejected after stop begins");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    private sealed class OneShotSuspendingInterceptor : ISharpLinkClientInterceptor
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remaining = 1;

        internal Task Entered => _entered.Task;
        internal void Release() => _release.TrySetResult();

        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            if (Interlocked.Exchange(ref _remaining, 0) != 0)
            {
                _entered.TrySetResult();
                await _release.Task.ConfigureAwait(false);
            }
            return await next(context).ConfigureAwait(false);
        }
    }

    private sealed class RecordingDelayPolicy(TimeSpan delay) : ISharpLinkRetryPolicy
    {
        private readonly TaskCompletionSource _evaluationStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        internal Task EvaluationStarted => _evaluationStarted.Task;
        internal int Count => Volatile.Read(ref _count);

        public SharpLinkRetryDecision Evaluate(in SharpLinkRetryContext context)
        {
            Interlocked.Increment(ref _count);
            _evaluationStarted.TrySetResult();
            return new SharpLinkRetryDecision(true, delay);
        }
    }

    private sealed class AlwaysRetryPolicy : ISharpLinkRetryPolicy
    {
        public SharpLinkRetryDecision Evaluate(in SharpLinkRetryContext context)
            => new(true, TimeSpan.Zero);
    }

    private sealed class ThrowingRetryPolicy : ISharpLinkRetryPolicy
    {
        public SharpLinkRetryDecision Evaluate(in SharpLinkRetryContext context)
            => throw new InvalidOperationException("injected runtime retry policy failure");
    }

    private static Exception CaptureException(Action action)
    {
        try
        {
            action();
            throw new Exception("expected exception");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
