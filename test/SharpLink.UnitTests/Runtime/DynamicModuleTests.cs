using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using SharpLink.Server;

namespace SharpLink.UnitTests.Runtime;

public class DynamicModuleTests
{
    [Test]
    public void DrainShouldWaitUntilEveryConcurrentLeaseIsReleased()
    {
        var module = new SharpLinkDynamicModule(
            typeof(DynamicModuleTests).Assembly,
            new EmptyManifest());
        Ensure(module.TryAcquire(stream: false, out var first), "first lease");
        Ensure(module.TryAcquire(stream: false, out var second), "second lease");

        module.TryBeginDraining();
        first.Dispose();
        Ensure(!module.WaitForDrainAsync().IsCompleted,
            "first completion cannot release a module with another active call");

        second.Dispose();
        Ensure(module.WaitForDrainAsync().IsCompletedSuccessfully,
            "last completion releases the drained module");
    }

    [Test]
    public void ReleasedModuleCancellationTokenShouldRemainSafeForStaleRouteReaders()
    {
        var module = new SharpLinkDynamicModule(
            typeof(DynamicModuleTests).Assembly,
            new EmptyManifest());
        module.TryBeginDraining();
        module.MarkReleased();

        var token = module.ForcedCancellation;
        using var callState = ServerCallCancellationState.Rent(
            requestId: 1,
            deadline: null,
            deadlineTimestamp: 0,
            connectionClosedToken: CancellationToken.None,
            serverStoppingToken: CancellationToken.None,
            moduleDrainingToken: token,
            supportsCooperativeCancellation: true);

        Ensure(callState.InvocationToken.CanBeCanceled,
            "stale route readers can safely register module cancellation after release");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class EmptyManifest : ISharpLinkGeneratedAssemblyManifest
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
