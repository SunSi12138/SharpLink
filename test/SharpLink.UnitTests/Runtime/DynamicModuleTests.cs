using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.Server;
using SharpLink.UnitTests.Client;

namespace SharpLink.UnitTests.Runtime;

public class DynamicModuleTests
{
    private const string LifetimeSourceTag = "rpc.sharplink.lifetime_source";

    [Test]
    public void DrainShouldBlockNewLeasesAndWaitUntilEveryConcurrentLeaseIsReleased()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var manifest = new EmptyManifest();
        using var registration = context.PrepareGeneratedManifest(manifest);
        var module = new SharpLinkDynamicModule(
            typeof(DynamicModuleTests).Assembly,
            manifest,
            registration);
        Ensure(module.TryAcquire(stream: false, out var first), "first lease");
        Ensure(module.TryAcquire(stream: false, out var second), "second lease");
        module.AssertAccountingInvariant();
        Ensure(module.RemainingCalls == 2 && module.RemainingStreams == 0,
            "two non-stream leases must occupy exactly two call counters");

        Ensure(module.TryBeginDraining(), "draining transition must publish once");
        Ensure(!module.TryAcquire(stream: true, out var rejected),
            "the drain barrier must reject a new stream lease");
        Ensure(!rejected.IsAcquired && module.RemainingCalls == 2 && module.RemainingStreams == 0,
            "a rejected post-drain acquire must not change either striped aggregate");
        module.AssertAccountingInvariant();
        first.Dispose();
        module.AssertAccountingInvariant();
        Ensure(!module.WaitForDrainAsync().IsCompleted,
            "first completion cannot release a module with another active call");

        second.Dispose();
        module.AssertAccountingInvariant();
        Ensure(module.RemainingCalls == 0 && module.RemainingStreams == 0,
            "the final lease must release exactly the counters it acquired");
        Ensure(module.WaitForDrainAsync().IsCompletedSuccessfully,
            "last completion releases the drained module");
    }

    [Test]
    public void ReleasedModuleCancellationTokenShouldRemainSafeForStaleRouteReaders()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var manifest = new EmptyManifest();
        using var registration = context.PrepareGeneratedManifest(manifest);
        var module = new SharpLinkDynamicModule(
            typeof(DynamicModuleTests).Assembly,
            manifest,
            registration);
        module.TryBeginDraining();
        module.MarkReleased();

        var token = module.ForcedCancellation;
        using var callState = ServerCallCancellationState.Rent(
            requestId: 1,
            deadline: default,
            timeProvider: TimeProvider.System,
            connectionClosedToken: CancellationToken.None,
            serverStoppingToken: CancellationToken.None,
            moduleDrainingToken: token,
            supportsCooperativeCancellation: true);

        Ensure(callState.InvocationToken.CanBeCanceled,
            "stale route readers can safely register module cancellation after release");
    }

    [Test]
    public async Task ProviderAwareDrainShouldTimeOutAtExactEqualityAndReleaseItsTimer()
    {
        var provider = new ManualTimeProvider();
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(provider)
            .Build(includeGeneratedAssemblyCatalog: false);
        var manifest = new EmptyManifest();
        using var registration = context.PrepareGeneratedManifest(manifest);
        var module = new SharpLinkDynamicModule(
            typeof(DynamicModuleTests).Assembly,
            manifest,
            registration);
        Ensure(module.TryAcquire(stream: true, out var lease),
            "the timeout scenario must retain one call and stream lease");
        Ensure(module.TryBeginDraining(),
            "the module must publish Draining before the bounded wait");
        var wait = SharpLinkDynamicModule.WaitForDrainAsync(
            module.WaitForDrainAsync(),
            TimeSpan.FromSeconds(5),
            provider);

        Ensure(provider.ActiveTimerCount == 1,
            "the pending module drain must own one provider timer");
        provider.Advance(TimeSpan.FromSeconds(5).Subtract(TimeSpan.FromTicks(1)));
        await Task.Yield();
        Ensure(!wait.IsCompleted,
            "one provider tick before the graceful boundary must remain pending");
        Ensure(module.RemainingCalls == 1 && module.RemainingStreams == 1,
            "fake-time advancement must not release the retained module lease");

        provider.Advance(TimeSpan.FromTicks(1));
        Ensure(!await wait,
            "an undrained module must time out at exact provider equality");
        Ensure(module.State == SharpLinkDynamicModuleState.Draining &&
               module.RemainingCalls == 1 && module.RemainingStreams == 1,
            "the bounded wait helper must not mutate module state or counters by itself");
        Ensure(provider.ActiveTimerCount == 0,
            "the timed-out drain must dispose its provider timer");

        lease.Dispose();
        await module.WaitForDrainAsync();
        Ensure(module.RemainingCalls == 0 && module.RemainingStreams == 0,
            "the final lease must still drain both counters after timeout");
    }

    [Test]
    public async Task ProviderAwareDrainShouldCompleteOnLeaseReleaseBeforeBoundaryAndDisarmTimeout()
    {
        var provider = new ManualTimeProvider();
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(provider)
            .Build(includeGeneratedAssemblyCatalog: false);
        var manifest = new EmptyManifest();
        using var registration = context.PrepareGeneratedManifest(manifest);
        var module = new SharpLinkDynamicModule(
            typeof(DynamicModuleTests).Assembly,
            manifest,
            registration);
        Ensure(module.TryAcquire(stream: false, out var lease),
            "the release scenario must retain one call lease");
        module.TryBeginDraining();
        var wait = SharpLinkDynamicModule.WaitForDrainAsync(
            module.WaitForDrainAsync(),
            TimeSpan.FromSeconds(5),
            provider);

        provider.Advance(TimeSpan.FromSeconds(5).Subtract(TimeSpan.FromTicks(1)));
        await Task.Yield();
        Ensure(!wait.IsCompleted && provider.ActiveTimerCount == 1,
            "the drain must remain pending with one owned timer before lease release");

        lease.Dispose();
        Ensure(await wait,
            "the final lease release immediately before the boundary must complete the drain");
        Ensure(module.WaitForDrainAsync().IsCompletedSuccessfully &&
               module.RemainingCalls == 0 && module.RemainingStreams == 0,
            "lease release must publish drained state with balanced counters");
        await provider.WaitForTimersDrainedAsync();
        Ensure(provider.ActiveTimerCount == 0,
            "successful drain completion must disarm the losing timeout timer");

        provider.Advance(TimeSpan.FromHours(1));
        Ensure(wait.IsCompletedSuccessfully && wait.Result,
            "later fake-time advancement must not change a successful drain result");
    }

    [Test]
    [NotInParallel("client-lifetime-telemetry")]
    public async Task DynamicServerStreamExpiredInheritedBudgetShouldEmitFailedLogicalCallBeforeEnumeration()
    {
        const int methodId = 1901;
        Activity? logicalActivity = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "SharpLink.Client",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.DisplayName == "sharplink.rpc" &&
                    string.Equals(
                        activity.GetTagItem("rpc.sharplink.method_id")?.ToString(),
                        methodId.ToString(),
                        StringComparison.Ordinal))
                {
                    logicalActivity = activity;
                }
            }
        };
        ActivitySource.AddActivityListener(listener);

        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseRequestTimeout());
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var manifest = new EmptyManifest();
        using var registration = context.PrepareGeneratedManifest(manifest);
        var module = new SharpLinkDynamicModule(
            typeof(DynamicModuleTests).Assembly,
            manifest,
            registration);
        var channel = new SharpLinkModuleRpcChannel(client, module);

        var provider = new ManualTimeProvider();
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), provider);
        provider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(2));
        using var parent = SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(
            "parent",
            null,
            deadline,
            provider));

        var method = new RpcMethodDescriptor(
            ContractId: 1,
            MethodId: methodId,
            Kind: RpcMethodKind.ServerStreaming,
            HasResponsePayload: true,
            HasClientStreams: false,
            HasMethodTimeout: false,
            MethodTimeout: null);
        var request = default(RpcEmptyRequest);
        SharpLinkException? failure = null;
        try
        {
            _ = channel.InvokeServerStreamingAsync(
                method,
                in request,
                RpcEmptyRequestCodec.Instance,
                channel.RuntimeContext.Codecs.GetCodec<int>(),
                metadata: null,
                cancellationToken: default);
        }
        catch (SharpLinkException exception)
        {
            failure = exception;
        }

        Ensure(failure?.Code == SharpLinkErrorCode.DeadlineExceeded,
            "expired inherited budget should terminate dynamic server streaming before deferred enumeration");
        var activity = logicalActivity ?? throw new Exception(
            "expired inherited budget should emit a logical client activity for dynamic server streaming");
        Ensure(activity.GetTagItem(LifetimeSourceTag)?.ToString() == "inherited_time_budget",
            "dynamic server streaming expired budget lifetime source");
        Ensure(activity.Status == ActivityStatusCode.Error,
            "dynamic server streaming expired budget logical activity should fail");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class EmptyManifest : ITestGeneratedManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(DynamicModuleTests).Assembly;
        public string CompileTimeDescriptor => "test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }
}
