using SharpLink.Client;
using SharpLink.UnitTests.Runtime;
using static SharpLink.UnitTests.Client.SharpLinkClientRetryBehaviorSupport;
using static SharpLink.UnitTests.Client.SharpLinkClientRetrySharedSupport;

namespace SharpLink.UnitTests.Client;

public sealed class EndpointSelectionRuntimeInteractionTests
{
    [Test]
    public async Task RetryNextPhysicalAttemptShouldCaptureLatestSelectionPolicy()
    {
        var first = new TestClientTransportFactory();
        var second = new TestClientTransportFactory();
        var third = new TestClientTransportFactory();
        var endpoints = new[]
        {
            new StaticEndpointConfiguration(Endpoint("first", 6401), first),
            new StaticEndpointConfiguration(Endpoint("second", 6402), second),
            new StaticEndpointConfiguration(Endpoint("third", 6403), third)
        };
        await using var client = ClientBuilderTestHelper.BuildStatic(endpoints, builder =>
        {
            ConfigureThreeEndpointCluster(builder);
            builder.UseEndpointSelector(new FirstUnexcludedSelector());
            ConfigureRetry(builder, RetryOptions(2, TimeSpan.Zero));
        });
        await client.ConnectAsync();
        await WaitForReadyConnectionCountAsync(client, 3);

        var invocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(client).AsTask();
        var firstAttempt = await first.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);

        client.UpdateEndpointSelector(new FixedIndexSelector(2));
        await InjectErrorAsync(first, firstAttempt, SharpLinkErrorCode.Unavailable);

        var secondAttempt = await third.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await third.Connection.InjectInt32ResponseAsync(unchecked((long)secondAttempt.RequestId));

        Ensure(await invocation == 0, "retry response after runtime selector publication");
        Ensure(!await second.Connection.TryWaitForSentPacket(
            ProtocolV2FrameType.Request,
            TimeSpan.FromMilliseconds(100)),
            "the later physical retry attempt must use the latest selector generation rather than the old fallback order");
    }

    [Test]
    public async Task AdmissionReselectionShouldKeepOneCapturedPolicyGeneration()
    {
        var first = new TestClientTransportFactory();
        var second = new TestClientTransportFactory();
        var third = new TestClientTransportFactory();
        var admission = new GatedRejectFirstAdmissionPolicy();
        var endpoints = new[]
        {
            new StaticEndpointConfiguration(Endpoint("first", 6411), first),
            new StaticEndpointConfiguration(Endpoint("second", 6412), second),
            new StaticEndpointConfiguration(Endpoint("third", 6413), third)
        };
        await using var client = ClientBuilderTestHelper.BuildStatic(endpoints, builder =>
        {
            ConfigureThreeEndpointCluster(builder);
            builder.UseEndpointSelector(new FirstUnexcludedSelector());
            builder.UseEndpointAdmission(admission);
        });
        await client.ConnectAsync();
        await WaitForReadyConnectionCountAsync(client, 3);

        var invocation = Task.Run(async () =>
            await ClientInvokerTestHelper.InvokeUnaryAsync(client).ConfigureAwait(false));
        await admission.Entered.WaitAsync(TimeSpan.FromSeconds(2));

        client.UpdateEndpointSelector(new FixedIndexSelector(2));
        admission.Release();

        var admitted = await second.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await second.Connection.InjectInt32ResponseAsync(unchecked((long)admitted.RequestId));

        Ensure(await invocation == 0,
            "the admission-rejected local reselection should complete on the old generation's next candidate");
        Ensure(!await third.Connection.TryWaitForSentPacket(
            ProtocolV2FrameType.Request,
            TimeSpan.FromMilliseconds(100)),
            "a publication racing one physical attempt must not be recaptured inside its admission/reselection loop");
    }

    [Test]
    public async Task DisposeRaceShouldSealSelectionPublicationAtOneLifecycleBoundary()
    {
        var first = new TestClientTransportFactory();
        var second = new TestClientTransportFactory();
        var endpoints = new[]
        {
            new StaticEndpointConfiguration(Endpoint("first", 6421), first),
            new StaticEndpointConfiguration(Endpoint("second", 6422), second)
        };
        var client = ClientBuilderTestHelper.BuildStatic(endpoints, builder => builder.UseCluster(_ => { }));
        var successfulPublications = 0;
        try
        {
            var writer = Task.Run(() =>
            {
                var iteration = 0;
                while (true)
                {
                    try
                    {
                        client.UpdateLoadBalancing((iteration++ & 1) == 0
                            ? SharpLinkLoadBalancingStrategy.Random
                            : SharpLinkLoadBalancingStrategy.RoundRobin);
                        Interlocked.Increment(ref successfulPublications);
                    }
                    catch (InvalidOperationException)
                    {
                        return;
                    }
                }
            });

            while (Volatile.Read(ref successfulPublications) < 128)
                await Task.Yield();

            await client.DisposeAsync();
            await writer.WaitAsync(TimeSpan.FromSeconds(2));

            var sealedSnapshot = client.GetEndpointSelectionPolicySnapshot();
            EnsureThrows<InvalidOperationException>(() =>
                client.UpdateLoadBalancing(SharpLinkLoadBalancingStrategy.LeastPending));
            Ensure(client.GetEndpointSelectionPolicySnapshot() == sealedSnapshot,
                "Dispose must seal publication without allowing a post-seal generation to appear");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    private static void ConfigureThreeEndpointCluster(SharpClientBuilder builder)
        => builder.UseCluster(options =>
        {
            options.MinReadyEndpoints = 3;
            options.MaxConnections = 3;
            options.MaxConnectionsPerEndpoint = 1;
        });

    private sealed class FixedIndexSelector(int index) : ISharpLinkEndpointSelector
    {
        public int Select(in SharpLinkEndpointSelectionContext context) => index;
    }

    private sealed class GatedRejectFirstAdmissionPolicy : ISharpLinkEndpointAdmissionPolicy
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new(initialState: false);

        public Task Entered => _entered.Task;

        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint,
            in RpcMethodDescriptor method)
        {
            if (endpoint.Endpoint.Id == "first")
            {
                _entered.TrySetResult();
                if (!_release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The admission/reselection race was not released.");
                return new SharpLinkEndpointAdmissionDecision(false, Token: 0, RetryAfter: null);
            }

            return new SharpLinkEndpointAdmissionDecision(true, Token: 1, RetryAfter: null);
        }

        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
        }

        public void Release() => _release.Set();
    }

    private static void EnsureThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new Exception($"expected {typeof(TException).Name}");
    }
}
