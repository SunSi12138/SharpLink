using System.Threading;
using System.Diagnostics;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;
using static SharpLink.UnitTests.Client.SharpLinkClientRetryBehaviorSupport;
using static SharpLink.UnitTests.Client.SharpLinkClientRetrySharedSupport;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientRetryBehaviorTests
{
    [Test]
    public async Task IdempotentUnaryShouldRetryRemoteUnavailableAndExposeResponseObservation()
    {
        var transport = new TestClientTransportFactory();
        var policy = new RecordingRetryPolicy();
        await using var client = CreateRetryClient(
            transport, policy, maxAttempts: 2, requestTimeout: TimeSpan.FromSeconds(1));
        await client.ConnectAsync();

        var invocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(client).AsTask();
        var first = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await InjectErrorAsync(transport, first, SharpLinkErrorCode.Unavailable);
        var second = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)second.RequestId));

        Ensure(await invocation == 0, "second attempt result");
        Ensure(policy.Count == 1, "policy invocation count");
        Ensure(policy.LastContext.Attempt == 1, "first completed attempt");
        Ensure(policy.LastContext.ErrorCode == SharpLinkErrorCode.Unavailable, "remote unavailable code");
        Ensure(policy.LastContext.ResponseObserved, "remote error is an observed response");
    }

    [Test]
    public async Task NonIdempotentUnaryAndResourceExhaustedShouldNotRetry()
    {
        var transport = new TestClientTransportFactory();
        await using var client = CreateRetryClient(transport, policy: null, maxAttempts: 3);
        await client.ConnectAsync();

        var nonIdempotent = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        var first = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await InjectErrorAsync(transport, first, SharpLinkErrorCode.Unavailable);
        var nonIdempotentError = await EnsureThrows<SharpLinkException>(nonIdempotent);
        Ensure(nonIdempotentError.Code == SharpLinkErrorCode.Unavailable, "non-idempotent result");
        Ensure(!await transport.Connection.TryWaitForSentPacket(
            ProtocolV2FrameType.Request, TimeSpan.FromMilliseconds(100)), "non-idempotent no second request");

        var idempotent = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(client).AsTask();
        var resourceExhausted = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await InjectErrorAsync(transport, resourceExhausted, SharpLinkErrorCode.ResourceExhausted);
        var resourceError = await EnsureThrows<SharpLinkException>(idempotent);
        Ensure(resourceError.Code == SharpLinkErrorCode.ResourceExhausted, "resource exhausted result");
        Ensure(!await transport.Connection.TryWaitForSentPacket(
            ProtocolV2FrameType.Request, TimeSpan.FromMilliseconds(100)), "resource exhausted no second request");
    }

    [Test]
    public async Task RetryShouldHonorDeadlineAndCancellationDuringDelay()
    {
        var deadlineProvider = new ManualTimeProvider();
        var deadlineTransport = new TestClientTransportFactory();
        var deadlinePolicy = new DelayingRetryPolicy(TimeSpan.FromSeconds(5));
        await using var deadlineClient = ClientBuilderTestHelper.Build(deadlineTransport, builder =>
        {
            builder.UseTimeProvider(deadlineProvider);
            ConfigureRetry(builder, RetryOptions(2, TimeSpan.Zero));
            builder.UseRetry(deadlinePolicy);
            builder.UseRequestTimeout(TimeSpan.FromSeconds(5));
        });
        await deadlineClient.ConnectAsync();

        var deadlineInvocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(deadlineClient).AsTask();
        var deadlineRequest = await deadlineTransport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await InjectErrorAsync(deadlineTransport, deadlineRequest, SharpLinkErrorCode.Unavailable);
        await deadlinePolicy.EvaluationStarted.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(!deadlineInvocation.IsCompleted,
            "the retry delay must remain pending before the fake deadline advances");
        deadlineProvider.Advance(TimeSpan.FromSeconds(5));
        var deadlineError = await EnsureThrows<SharpLinkException>(deadlineInvocation);
        Ensure(deadlineError.Code == SharpLinkErrorCode.DeadlineExceeded, "retry delay deadline result");
        Ensure(!await deadlineTransport.Connection.TryWaitForSentPacket(
            ProtocolV2FrameType.Request, TimeSpan.FromMilliseconds(100)), "deadline no second request");

        var cancellationTransport = new TestClientTransportFactory();
        await using var cancellationClient = CreateRetryClient(
            cancellationTransport, policy: null, maxAttempts: 2, initialBackoff: TimeSpan.FromSeconds(1));
        await cancellationClient.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        var cancellationInvocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(
            cancellationClient, cancellationToken: cancellation.Token).AsTask();
        var cancellationRequest = await cancellationTransport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await InjectErrorAsync(cancellationTransport, cancellationRequest, SharpLinkErrorCode.Unavailable);
        cancellation.Cancel();
        await EnsureThrows<OperationCanceledException>(cancellationInvocation);
        Ensure(!await cancellationTransport.Connection.TryWaitForSentPacket(
            ProtocolV2FrameType.Request, TimeSpan.FromMilliseconds(100)), "cancel no second request");
    }

    [Test]
    public async Task ClientStopShouldCancelCustomRetryBackoffPromptly()
    {
        var transport = new TestClientTransportFactory();
        var policy = new DelayingRetryPolicy(TimeSpan.MaxValue);
        await using var client = CreateRetryClient(transport, policy, maxAttempts: 2);
        await client.ConnectAsync();

        var invocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(client).AsTask();
        var request = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await InjectErrorAsync(transport, request, SharpLinkErrorCode.Unavailable);
        await policy.EvaluationStarted.WaitAsync(TimeSpan.FromSeconds(2));

        var stoppedAt = Stopwatch.GetTimestamp();
        var stop = client.StopAsync().AsTask();
        var exception = await EnsureThrows<SharpLinkException>(
            invocation.WaitAsync(TimeSpan.FromSeconds(2)));
        await stop.WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(exception.Code == SharpLinkErrorCode.ConnectionClosed, "stopped retry backoff error code");
        Ensure(Stopwatch.GetElapsedTime(stoppedAt) < TimeSpan.FromSeconds(1),
            "client stop must cancel the custom retry backoff promptly");
    }

    [Test]
    public async Task LogicalInvocationShouldRemainActiveBetweenRetryAttempts()
    {
        var transport = new TestClientTransportFactory();
        var policy = new DelayingRetryPolicy(TimeSpan.MaxValue);
        await using var client = CreateRetryClient(transport, policy, maxAttempts: 2);
        await client.ConnectAsync();
        using var cancellation = new CancellationTokenSource();

        var invocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(
            client, cancellationToken: cancellation.Token).AsTask();
        var request = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await InjectErrorAsync(transport, request, SharpLinkErrorCode.Unavailable);
        await policy.EvaluationStarted.WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(client.ActiveClientCallCount == 0,
            "no connection-level attempt should remain active during retry backoff");
        Ensure(((ISharpLinkClientDrainInspector)client).ActiveCallCount == 1,
            "the complete logical invocation must remain visible between retry attempts");

        cancellation.Cancel();
        await EnsureThrows<OperationCanceledException>(invocation);
        Ensure(((ISharpLinkClientDrainInspector)client).ActiveCallCount == 0,
            "the logical invocation count must be released after cancellation");
    }

    [Test]
    public async Task HugeBuiltInJitteredRetryDelayShouldRemainCancellable()
    {
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var provider = new ManualTimeProvider();
            var transport = new TestClientTransportFactory();
            await using var client = ClientBuilderTestHelper.Build(transport, builder =>
            {
                builder.UseTimeProvider(provider);
                builder.DisableRequestTimeout();
                builder.UseRetry(options =>
                {
                    options.MaxAttempts = 2;
                    options.InitialBackoff = TimeSpan.MaxValue;
                    options.MaxBackoff = TimeSpan.MaxValue;
                    options.JitterRatio = 1;
                });
            });
            await client.ConnectAsync();
            while (provider.ActiveTimerCount == 0)
                await Task.Yield();
            var baselineTimerCount = provider.ActiveTimerCount;

            var invocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(client).AsTask();
            var request = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
            await InjectErrorAsync(transport, request, SharpLinkErrorCode.Unavailable);
            while (provider.ActiveTimerCount <= baselineTimerCount)
                await Task.Yield();

            var stop = client.StopAsync().AsTask();
            var exception = await EnsureThrows<SharpLinkException>(
                invocation.WaitAsync(TimeSpan.FromSeconds(2)));
            await stop.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(exception.Code == SharpLinkErrorCode.ConnectionClosed,
                $"huge jittered retry delay cancellation iteration {iteration}");
        }
    }

    [Test]
    public async Task RetryDelayBeyondDeadlineShouldNotOverflow()
    {
        var provider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        var policy = new HugeDelayPolicy();
        await using var client = ClientBuilderTestHelper.Build(transport, builder =>
        {
            builder.UseTimeProvider(provider);
            ConfigureRetry(builder, RetryOptions(2, TimeSpan.Zero));
            builder.UseRetry(policy);
            builder.UseRequestTimeout(TimeSpan.FromSeconds(5));
        });
        await client.ConnectAsync();

        var invocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(client).AsTask();
        var request = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await InjectErrorAsync(transport, request, SharpLinkErrorCode.Unavailable);
        await policy.EvaluationStarted.WaitAsync(TimeSpan.FromSeconds(2));
        provider.Advance(TimeSpan.FromSeconds(5));

        var exception = await EnsureThrows<SharpLinkException>(invocation);
        Ensure(exception.Code == SharpLinkErrorCode.DeadlineExceeded,
            "oversized retry delay must remain bounded by the frozen deadline without overflowing");
        Ensure(policy.Count == 1, "custom retry policy should be evaluated once");
    }

    [Test]
    public async Task RetryShouldRunInterceptorOnceAndRejectInvalidCustomPolicyDelay()
    {
        var transport = new TestClientTransportFactory();
        var interceptor = new CountingInterceptor();
        var invalidPolicy = new NegativeDelayPolicy();
        await using var client = ClientBuilderTestHelper.Build(transport, builder =>
        {
            builder.AddInterceptor(interceptor);
            ConfigureRetry(builder, RetryOptions(2, TimeSpan.Zero));
            builder.UseRetry(invalidPolicy);
        });
        await client.ConnectAsync();

        var invocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(client).AsTask();
        var request = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await InjectErrorAsync(transport, request, SharpLinkErrorCode.Unavailable);
        var error = await EnsureThrows<SharpLinkException>(invocation);

        Ensure(error.Code == SharpLinkErrorCode.FailedPrecondition, "negative delay rejected");
        Ensure(interceptor.Count == 1, "interceptor runs once for logical call");
        Ensure(invalidPolicy.Count == 1, "custom policy receives failed attempt");
    }

    [Test]
    public async Task RetryShouldExcludeTriedEndpointsThenResetAfterAllCandidates()
    {
        var first = new TestClientTransportFactory();
        var second = new TestClientTransportFactory();
        var endpoints = new[]
        {
            new StaticEndpointConfiguration(Endpoint("first", 5001), first),
            new StaticEndpointConfiguration(Endpoint("second", 5002), second)
        };
        await using var client = ClientBuilderTestHelper.BuildStatic(endpoints, builder =>
        {
            builder.UseCluster(_ => { });
            builder.UseEndpointSelector(new FirstAvailableSelector());
            ConfigureRetry(builder, RetryOptions(3, TimeSpan.Zero));
        });
        await client.ConnectAsync();
        await WaitForReadyConnectionCountAsync(client, 2);

        var invocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(client).AsTask();
        var firstAttempt = await first.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await InjectErrorAsync(first, firstAttempt, SharpLinkErrorCode.Unavailable);
        var secondAttempt = await second.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await InjectErrorAsync(second, secondAttempt, SharpLinkErrorCode.Unavailable);
        var thirdAttempt = await first.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await first.Connection.InjectInt32ResponseAsync(unchecked((long)thirdAttempt.RequestId));

        Ensure(await invocation == 0, "candidate reset third attempt result");
    }

    [Test]
    public async Task EndpointAdmissionShouldRejectOneCandidateAndReportTheSelectedAttemptOnce()
    {
        var first = new TestClientTransportFactory();
        var second = new TestClientTransportFactory();
        var policy = new RejectFirstEndpointPolicy();
        var endpoints = new[]
        {
            new StaticEndpointConfiguration(Endpoint("first", 5001), first),
            new StaticEndpointConfiguration(Endpoint("second", 5002), second)
        };
        await using var client = ClientBuilderTestHelper.BuildStatic(endpoints, builder =>
        {
            builder.UseCluster(_ => { });
            builder.UseEndpointSelector(new FirstAvailableSelector());
            builder.UseEndpointAdmission(policy);
        });
        await client.ConnectAsync();
        await WaitForReadyConnectionCountAsync(client, 2);

        var invocation = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        var request = await second.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await second.Connection.InjectInt32ResponseAsync(unchecked((long)request.RequestId));

        Ensure(await invocation == 0, "admitted endpoint response");
        Ensure(policy.AcquireCount == 2, "both candidates evaluated");
        Ensure(policy.ReportCount == 1, $"only selected endpoint reported: {policy.ReportCount}");
        Ensure(policy.LastOutcome.Endpoint.Endpoint.Id == "second", "selected endpoint report identity");
        Ensure(policy.LastOutcome.Kind == SharpLinkEndpointOutcomeKind.Success, "selected endpoint report outcome");
        Ensure(!await first.Connection.TryWaitForSentPacket(
            ProtocolV2FrameType.Request, TimeSpan.FromMilliseconds(100)), "rejected endpoint has no request");
    }

    [Test]
    public async Task RetryShouldHonorAdmissionRetryAfterBeforeTheNextAttempt()
    {
        var transport = new TestClientTransportFactory();
        var admission = new RejectOnceWithRetryAfterPolicy(TimeSpan.FromMilliseconds(100));
        await using var client = ClientBuilderTestHelper.BuildEndpoint(
            Endpoint("retry", 5001), transport, builder =>
            {
                ConfigureRetry(builder, RetryOptions(2, TimeSpan.Zero));
                builder.UseEndpointAdmission(admission);
            });
        await client.ConnectAsync();

        var started = Stopwatch.GetTimestamp();
        var invocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(client).AsTask();
        var request = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        Ensure(Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(75),
            "retry must wait for the admission retry delay rather than consume the next attempt immediately");
        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)request.RequestId));

        Ensure(await invocation == 0, "admitted retry result");
        Ensure(admission.AcquireCount == 2, "admission should be retried once after its requested delay");
        Ensure(admission.ReportCount == 1, "only the admitted retry should report");
    }

    [Test]
    public async Task RetryShouldNotDelayUntriedEndpointsAfterAnAdmittedAttemptFails()
    {
        var first = new TestClientTransportFactory();
        var second = new TestClientTransportFactory();
        var third = new TestClientTransportFactory();
        var endpoints = new[]
        {
            new StaticEndpointConfiguration(Endpoint("first", 5001), first),
            new StaticEndpointConfiguration(Endpoint("second", 5002), second),
            new StaticEndpointConfiguration(Endpoint("third", 5003), third)
        };
        await using var client = ClientBuilderTestHelper.BuildStatic(endpoints, builder =>
        {
            builder.UseCluster(options =>
            {
                options.MinReadyEndpoints = 3;
                options.MaxConnections = 3;
            });
            builder.UseEndpointSelector(new FirstUnexcludedSelector());
            ConfigureRetry(builder, RetryOptions(2, TimeSpan.Zero));
            builder.UseEndpointAdmission(new RejectFirstEndpointWithDelayPolicy(TimeSpan.FromSeconds(30)));
        });
        await client.ConnectAsync();
        await WaitForReadyConnectionCountAsync(client, 3);

        var invocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(client).AsTask();
        var secondRequest = await second.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await InjectErrorAsync(second, secondRequest, SharpLinkErrorCode.Unavailable);
        var thirdRequest = await third.Connection.WaitForSentPacket(ProtocolV2FrameType.Request)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await third.Connection.InjectInt32ResponseAsync(unchecked((long)thirdRequest.RequestId));

        Ensure(await invocation == 0, "untried endpoint retry response");
    }
}
