using System.Threading;
using System.Diagnostics;
using SharpLink.Client;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Client;

public class SharpLinkClientRetryTests
{
    [Test]
    public async Task IdempotentUnaryShouldRetryRemoteUnavailableAndExposeResponseObservation()
    {
        var transport = new TestClientTransportFactory();
        var policy = new RecordingRetryPolicy();
        await using var client = CreateRetryClient(transport, policy, maxAttempts: 2);
        await client.ConnectAsync();

        var invocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(client).AsTask();
        var first = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await InjectErrorAsync(transport, first, SharpLinkErrorCode.Unavailable);
        var second = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await transport.Connection.InjectPacketAsync(
            ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, unchecked((long)second.RequestId));

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
    public async Task RetryShouldHonorAbsoluteDeadlineAndCancellationDuringDelay()
    {
        var deadlineTransport = new TestClientTransportFactory();
        await using var deadlineClient = CreateRetryClient(
            deadlineTransport, policy: null, maxAttempts: 2, initialBackoff: TimeSpan.FromMilliseconds(50));
        await deadlineClient.ConnectAsync();

        var deadlineInvocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(
            deadlineClient, new SharpLinkCallOptions { Timeout = TimeSpan.FromMilliseconds(20) }).AsTask();
        var deadlineRequest = await deadlineTransport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await InjectErrorAsync(deadlineTransport, deadlineRequest, SharpLinkErrorCode.Unavailable);
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
    public async Task HugeBuiltInJitteredRetryDelayShouldRemainCancellable()
    {
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var transport = new TestClientTransportFactory();
            await using var client = new SharpLinkClient(
                transport,
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30),
                retryOptions: new SharpLinkRetryOptions
                {
                    MaxAttempts = 2,
                    InitialBackoff = TimeSpan.MaxValue,
                    MaxBackoff = TimeSpan.MaxValue,
                    JitterRatio = 1
                });
            await client.ConnectAsync();

            var invocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(client).AsTask();
            var request = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
            await InjectErrorAsync(transport, request, SharpLinkErrorCode.Unavailable);
            await Task.Delay(20);

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
        var transport = new TestClientTransportFactory();
        var policy = new HugeDelayPolicy();
        await using var client = CreateRetryClient(transport, policy, maxAttempts: 2);
        await client.ConnectAsync();

        var invocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(
            client,
            new SharpLinkCallOptions { Timeout = TimeSpan.FromSeconds(1) }).AsTask();
        var request = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await InjectErrorAsync(transport, request, SharpLinkErrorCode.Unavailable);

        var exception = await EnsureThrows<SharpLinkException>(invocation);
        Ensure(exception.Code == SharpLinkErrorCode.DeadlineExceeded,
            "oversized retry delay must map to deadline exceeded instead of overflowing");
        Ensure(policy.Count == 1, "custom retry policy should be evaluated once");
    }

    [Test]
    public async Task RetryShouldRunInterceptorOnceAndRejectInvalidCustomPolicyDelay()
    {
        var transport = new TestClientTransportFactory();
        var interceptor = new CountingInterceptor();
        var invalidPolicy = new NegativeDelayPolicy();
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            clientInterceptors: [interceptor],
            retryOptions: RetryOptions(2, TimeSpan.Zero),
            retryPolicy: invalidPolicy);
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
        await using var client = new SharpLinkClient(
            first,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            staticEndpoints: endpoints,
            clusterOptions: new SharpLinkClusterOptions(),
            endpointSelector: new FirstAvailableSelector(),
            retryOptions: RetryOptions(3, TimeSpan.Zero));
        await client.ConnectAsync();
        await WaitForReadyConnectionCountAsync(client, 2);

        var invocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(client).AsTask();
        var firstAttempt = await first.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await InjectErrorAsync(first, firstAttempt, SharpLinkErrorCode.Unavailable);
        var secondAttempt = await second.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await InjectErrorAsync(second, secondAttempt, SharpLinkErrorCode.Unavailable);
        var thirdAttempt = await first.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await first.Connection.InjectPacketAsync(
            ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, unchecked((long)thirdAttempt.RequestId));

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
        await using var client = new SharpLinkClient(
            first,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            staticEndpoints: endpoints,
            clusterOptions: new SharpLinkClusterOptions(),
            endpointSelector: new FirstAvailableSelector(),
            endpointAdmissionPolicy: policy);
        await client.ConnectAsync();
        await WaitForReadyConnectionCountAsync(client, 2);

        var invocation = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        var request = await second.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await second.Connection.InjectPacketAsync(
            ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, unchecked((long)request.RequestId));

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
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            fixedEndpoint: Endpoint("retry", 5001),
            retryOptions: RetryOptions(2, TimeSpan.Zero),
            endpointAdmissionPolicy: admission);
        await client.ConnectAsync();

        var started = Stopwatch.GetTimestamp();
        var invocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(client).AsTask();
        var request = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        Ensure(Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(75),
            "retry must wait for the admission retry delay rather than consume the next attempt immediately");
        await transport.Connection.InjectPacketAsync(
            ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, unchecked((long)request.RequestId));

        Ensure(await invocation == 0, "admitted retry result");
        Ensure(admission.AcquireCount == 2, "admission should be retried once after its requested delay");
        Ensure(admission.ReportCount == 1, "only the admitted retry should report");
    }

    [Test]
    public async Task ClientStopShouldCancelRetryAdmissionDelayPromptly()
    {
        var transport = new TestClientTransportFactory();
        var admission = new SignaledRejectWithRetryAfterPolicy(TimeSpan.MaxValue);
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            fixedEndpoint: Endpoint("retry-admission", 5001),
            retryOptions: RetryOptions(2, TimeSpan.Zero),
            endpointAdmissionPolicy: admission);
        await client.ConnectAsync();

        var invocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(
            client, new SharpLinkCallOptions { WaitForReady = true }).AsTask();
        await admission.RejectionStarted.WaitAsync(TimeSpan.FromSeconds(2));

        var stoppedAt = Stopwatch.GetTimestamp();
        var stop = client.StopAsync().AsTask();
        var exception = await EnsureThrows<SharpLinkException>(
            invocation.WaitAsync(TimeSpan.FromSeconds(2)));
        await stop.WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(exception.Code == SharpLinkErrorCode.ConnectionClosed, "stopped retry admission error code");
        Ensure(Stopwatch.GetElapsedTime(stoppedAt) < TimeSpan.FromSeconds(1),
            "client stop must cancel the retry admission delay promptly");
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
        await using var client = new SharpLinkClient(
            first,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            staticEndpoints: endpoints,
            clusterOptions: new SharpLinkClusterOptions { MinReadyEndpoints = 3, MaxConnections = 3 },
            endpointSelector: new FirstUnexcludedSelector(),
            retryOptions: RetryOptions(2, TimeSpan.Zero),
            endpointAdmissionPolicy: new RejectFirstEndpointWithDelayPolicy(TimeSpan.FromSeconds(30)));
        await client.ConnectAsync();
        await WaitForReadyConnectionCountAsync(client, 3);

        var invocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(client).AsTask();
        var secondRequest = await second.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await InjectErrorAsync(second, secondRequest, SharpLinkErrorCode.Unavailable);
        var thirdRequest = await third.Connection.WaitForSentPacket(ProtocolV2FrameType.Request)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await third.Connection.InjectPacketAsync(
            ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, unchecked((long)thirdRequest.RequestId));

        Ensure(await invocation == 0, "untried endpoint retry response");
    }

    [Test]
    public void CircuitBreakerShouldRejectZeroFailureRatio()
    {
        try
        {
            _ = new SharpLinkCircuitBreakerOptions { FailureRatio = 0 }.CloneValidated();
            throw new Exception("zero failure ratio should be rejected");
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }

    [Test]
    public void CircuitBreakerShouldOpenPerGenerationForInfrastructureFailures()
    {
        var breaker = new SharpLinkCircuitBreaker(new SharpLinkCircuitBreakerOptions
        {
            MinimumThroughput = 2,
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(10),
            BreakDuration = TimeSpan.FromSeconds(10),
            HalfOpenMaxCalls = 1
        }.CloneValidated());
        var method = new RpcMethodDescriptor(1, 2, RpcMethodKind.Unary, true, false, false, null);
        var generationOne = new SharpLinkEndpointCandidate(Endpoint("breaker", 5001), 1, 0, generation: 1);
        var failure = new SharpLinkEndpointOutcome(
            generationOne,
            method,
            SharpLinkEndpointOutcomeKind.RemoteError,
            SharpLinkErrorCode.Unavailable,
            ResponseObserved: true,
            TimeSpan.Zero);

        var first = breaker.TryAcquire(generationOne, method);
        Ensure(first.IsAllowed, "closed breaker first acquisition");
        breaker.Report(failure, first.Token);
        var second = breaker.TryAcquire(generationOne, method);
        Ensure(second.IsAllowed, "closed breaker second acquisition");
        breaker.Report(failure, second.Token);

        var open = breaker.TryAcquire(generationOne, method);
        Ensure(!open.IsAllowed && open.RetryAfter > TimeSpan.Zero, "breaker opens after failure ratio threshold");
        var replacementGeneration = new SharpLinkEndpointCandidate(Endpoint("breaker", 5002), 1, 0, generation: 2);
        Ensure(breaker.TryAcquire(replacementGeneration, method).IsAllowed, "replacement generation starts closed");
    }

    [Test]
    public void CircuitBreakerShouldIgnoreLocalResourceExhaustionDuringHalfOpenProbe()
    {
        var breaker = new SharpLinkCircuitBreaker(new SharpLinkCircuitBreakerOptions
        {
            MinimumThroughput = 1,
            FailureRatio = 1,
            SamplingDuration = TimeSpan.FromSeconds(10),
            BreakDuration = TimeSpan.FromMilliseconds(1),
            HalfOpenMaxCalls = 1
        }.CloneValidated());
        var method = new RpcMethodDescriptor(1, 2, RpcMethodKind.Unary, true, false, false, null);
        var endpoint = new SharpLinkEndpointCandidate(Endpoint("breaker", 5001), 1, 0, generation: 1);
        var infrastructureFailure = new SharpLinkEndpointOutcome(
            endpoint, method, SharpLinkEndpointOutcomeKind.RemoteError, SharpLinkErrorCode.Unavailable, true, TimeSpan.Zero);
        var localCapacityFailure = new SharpLinkEndpointOutcome(
            endpoint, method, SharpLinkEndpointOutcomeKind.SendFailure, SharpLinkErrorCode.ResourceExhausted, false, TimeSpan.Zero);

        breaker.Report(infrastructureFailure, breaker.TryAcquire(endpoint, method).Token);
        Thread.Sleep(20);
        var probe = breaker.TryAcquire(endpoint, method);
        Ensure(probe.IsAllowed && probe.Token != 0, "half-open probe should be admitted");
        breaker.Report(localCapacityFailure, probe.Token);

        var nextProbe = breaker.TryAcquire(endpoint, method);
        Ensure(nextProbe.IsAllowed && nextProbe.Token != 0,
            "local capacity pressure must not close the breaker as a successful probe");
    }

    [Test]
    public void CircuitBreakerShouldIgnoreReportsFromAnExpiredHalfOpenEpoch()
    {
        var breaker = new SharpLinkCircuitBreaker(new SharpLinkCircuitBreakerOptions
        {
            MinimumThroughput = 1,
            FailureRatio = 1,
            SamplingDuration = TimeSpan.FromSeconds(10),
            BreakDuration = TimeSpan.FromMilliseconds(1),
            HalfOpenMaxCalls = 2
        }.CloneValidated());
        var method = new RpcMethodDescriptor(1, 2, RpcMethodKind.Unary, true, false, false, null);
        var endpoint = new SharpLinkEndpointCandidate(Endpoint("breaker", 5001), 1, 0, generation: 1);
        var failure = new SharpLinkEndpointOutcome(
            endpoint, method, SharpLinkEndpointOutcomeKind.RemoteError, SharpLinkErrorCode.Unavailable, true, TimeSpan.Zero);
        var success = new SharpLinkEndpointOutcome(
            endpoint, method, SharpLinkEndpointOutcomeKind.Success, null, true, TimeSpan.Zero);

        breaker.Report(failure, breaker.TryAcquire(endpoint, method).Token);
        Thread.Sleep(20);
        var firstEpochFirstProbe = breaker.TryAcquire(endpoint, method);
        var firstEpochSecondProbe = breaker.TryAcquire(endpoint, method);
        Ensure(firstEpochFirstProbe.IsAllowed && firstEpochFirstProbe.Token != 0, "first half-open probe token");
        Ensure(firstEpochSecondProbe.IsAllowed && firstEpochSecondProbe.Token == firstEpochFirstProbe.Token,
            "same half-open epoch token");

        breaker.Report(failure, firstEpochFirstProbe.Token);
        Thread.Sleep(20);
        var currentEpochProbe = breaker.TryAcquire(endpoint, method);
        Ensure(currentEpochProbe.IsAllowed && currentEpochProbe.Token != 0, "current half-open probe token");

        breaker.Report(success, firstEpochSecondProbe.Token);
        var stillHalfOpen = breaker.TryAcquire(endpoint, method);
        Ensure(stillHalfOpen.IsAllowed && stillHalfOpen.Token != 0,
            "stale success must not close a newer half-open epoch");
    }

    private static SharpLinkClient CreateRetryClient(
        TestClientTransportFactory transport,
        ISharpLinkRetryPolicy? policy,
        int maxAttempts,
        TimeSpan? initialBackoff = null)
        => new(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            retryOptions: RetryOptions(maxAttempts, initialBackoff ?? TimeSpan.Zero),
            retryPolicy: policy);

    private static SharpLinkRetryOptions RetryOptions(int maxAttempts, TimeSpan initialBackoff)
        => new()
        {
            MaxAttempts = maxAttempts,
            InitialBackoff = initialBackoff,
            MaxBackoff = initialBackoff,
            JitterRatio = 0
        };

    private static SharpLinkEndpoint Endpoint(string id, int port)
        => new()
        {
            Id = id,
            Address = new SharpLinkTcpAddress("127.0.0.1", port)
        };

    private static Task InjectErrorAsync(
        TestClientTransportFactory transport,
        ProtocolV2FrameHeader request,
        SharpLinkErrorCode code)
    {
        var payload = new PooledByteBufferWriter();
        ProtocolV2PayloadCodec.WriteError(payload, code, code.ToString(), 1024, out _);
        return transport.Connection.InjectFrameAsync(
            ProtocolV2FrameType.Response,
            ProtocolV2FrameFlags.Error,
            request.RequestId,
            payload.WrittenMemory);
    }

    private static async Task<TException> EnsureThrows<TException>(Task invocation)
        where TException : Exception
    {
        try
        {
            await invocation;
            throw new Exception($"expected {typeof(TException).Name}");
        }
        catch (TException exception)
        {
            return exception;
        }
    }

    private static async Task WaitForReadyConnectionCountAsync(SharpLinkClient client, int expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (client.ReadyConnectionCount < expected && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Ensure(client.ReadyConnectionCount >= expected, $"expected {expected} ready connections");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class RecordingRetryPolicy : ISharpLinkRetryPolicy
    {
        public int Count { get; private set; }
        public SharpLinkRetryContext LastContext { get; private set; }

        public SharpLinkRetryDecision Evaluate(in SharpLinkRetryContext context)
        {
            Count++;
            LastContext = context;
            return new SharpLinkRetryDecision(true, TimeSpan.Zero);
        }
    }

    private sealed class DelayingRetryPolicy(TimeSpan delay) : ISharpLinkRetryPolicy
    {
        private readonly TaskCompletionSource _evaluationStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EvaluationStarted => _evaluationStarted.Task;

        public SharpLinkRetryDecision Evaluate(in SharpLinkRetryContext context)
        {
            _evaluationStarted.TrySetResult();
            return new SharpLinkRetryDecision(true, delay);
        }
    }

    private sealed class NegativeDelayPolicy : ISharpLinkRetryPolicy
    {
        public int Count { get; private set; }

        public SharpLinkRetryDecision Evaluate(in SharpLinkRetryContext context)
        {
            Count++;
            return new SharpLinkRetryDecision(true, TimeSpan.FromMilliseconds(-1));
        }
    }

    private sealed class CountingInterceptor : ISharpLinkClientInterceptor
    {
        public int Count { get; private set; }

        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            Count++;
            return await next(context);
        }
    }

    private sealed class HugeDelayPolicy : ISharpLinkRetryPolicy
    {
        public int Count { get; private set; }

        public SharpLinkRetryDecision Evaluate(in SharpLinkRetryContext context)
        {
            Count++;
            return new SharpLinkRetryDecision(true, TimeSpan.MaxValue);
        }
    }

    private sealed class FirstAvailableSelector : ISharpLinkEndpointSelector
    {
        public int Select(in SharpLinkEndpointSelectionContext context)
            => (context.ExcludedMask & 1UL) == 0 ? 0 : 1;
    }

    private sealed class FirstUnexcludedSelector : ISharpLinkEndpointSelector
    {
        public int Select(in SharpLinkEndpointSelectionContext context)
        {
            for (var index = 0; index < context.Count; index++)
                if ((context.ExcludedMask & (1UL << index)) == 0)
                    return index;
            return -1;
        }
    }

    private sealed class RejectFirstEndpointPolicy : ISharpLinkEndpointAdmissionPolicy
    {
        public int AcquireCount { get; private set; }
        public int ReportCount { get; private set; }
        public SharpLinkEndpointOutcome LastOutcome { get; private set; }

        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint,
            in RpcMethodDescriptor method)
        {
            AcquireCount++;
            return endpoint.Endpoint.Id == "first"
                ? new SharpLinkEndpointAdmissionDecision(false, Token: 0, RetryAfter: null)
                : new SharpLinkEndpointAdmissionDecision(true, Token: 7, RetryAfter: null);
        }

        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
            ReportCount++;
            LastOutcome = outcome;
            Ensure(token == 7, "admission token preserved");
        }
    }

    private sealed class RejectOnceWithRetryAfterPolicy(TimeSpan retryAfter) : ISharpLinkEndpointAdmissionPolicy
    {
        public int AcquireCount { get; private set; }
        public int ReportCount { get; private set; }

        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint,
            in RpcMethodDescriptor method)
        {
            AcquireCount++;
            return AcquireCount == 1
                ? new SharpLinkEndpointAdmissionDecision(false, Token: 0, RetryAfter: retryAfter)
                : new SharpLinkEndpointAdmissionDecision(true, Token: 1, RetryAfter: null);
        }

        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
            ReportCount++;
            Ensure(token == 1, "admitted retry token");
        }
    }

    private sealed class SignaledRejectWithRetryAfterPolicy(TimeSpan retryAfter) : ISharpLinkEndpointAdmissionPolicy
    {
        private readonly TaskCompletionSource _rejectionStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RejectionStarted => _rejectionStarted.Task;

        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint,
            in RpcMethodDescriptor method)
        {
            _rejectionStarted.TrySetResult();
            return new SharpLinkEndpointAdmissionDecision(false, Token: 0, RetryAfter: retryAfter);
        }

        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
        }
    }

    private sealed class RejectFirstEndpointWithDelayPolicy(TimeSpan retryAfter) : ISharpLinkEndpointAdmissionPolicy
    {
        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint,
            in RpcMethodDescriptor method)
            => endpoint.Endpoint.Id == "first"
                ? new SharpLinkEndpointAdmissionDecision(false, Token: 0, RetryAfter: retryAfter)
                : new SharpLinkEndpointAdmissionDecision(true, Token: 1, RetryAfter: null);

        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
        }
    }
}
