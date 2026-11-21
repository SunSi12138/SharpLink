using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public async Task NStudyNormalFeedKeepsItsGeneratedStubAndCodecAcrossReplacement(string transport)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = guard.Token;
        var trace = new NStudyTrace(transport);
        var directory = GetProjectOutputDirectory("SharpLink.CodecPublicationDrainProvider");
        var path = Path.Combine(directory, "SharpLink.CodecPublicationDrainProvider.dll");
        var newLoader = new PluginLoadContext($"feed-server-r2-{transport}", directory);
        var clientLoader = new PluginLoadContext($"feed-client-{transport}", directory);
        try
        {
            var newAssembly = newLoader.LoadFromAssemblyPath(path);
            var clientAssembly = clientLoader.LoadFromAssemblyPath(path);
            var service = FeedType(newAssembly, "ReadingFeed");
            service.GetProperty("FirstValue")!.SetValue(null, 20);
            var load = SharpLinkAssemblyManifestLoader.TryLoad(typeof(IShutdownCleanupProbe).Assembly, out var manifest);
            Ensure(load.Succeeded, "generated unrelated service manifest loads");
            var bCid = manifest!.Contracts.Single(item => item.ContractType == typeof(IShutdownCleanupProbe)).ContractId;
            var source = new FixedGeneratedManifestSource([manifest]);
            await using var provider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
            var builder = NewNStudyServer(source, provider);
            var memoryName = transport == "shared-memory" ? $"sharplink-n-feed-{Guid.NewGuid():N}" : null;
            if (memoryName is null)
                builder.UseTcp(0, IPAddress.Loopback);
            else
                builder.UseSharedMemory(memoryName);
            var port = memoryName is null ? ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port : 0;
            await using var server = (SharpLinkServer)builder.Build();
            await using var client = NewNStudyClient(source, port, memoryName);
            Ensure(client.RegisterAssembly(clientAssembly).Succeeded, "client has the compatible generated assembly");
            var clientModule = GetDynamicModule(client, clientAssembly);
            var contract = clientModule.Manifest.Contracts.Single(item => item.ContractType == FeedType(clientAssembly, "IReadingFeed"));
            var feed = new NStudyFeedClient(contract.ContractType, FeedType(clientAssembly, "FeedReading"));
            var identity = new NStudyContractInputs(contract.ContractId, clientModule.Manifest.RpcAssemblyHash,
                bCid, manifest.RpcAssemblyHash);
            var (retired, a2, bBinding) = await ExerciseNStudyFeedReplacementAsync(server, client, directory, path,
                newAssembly, feed, identity, trace, memoryName, ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

            for (var i = 0; retired.IsAlive && i < 24; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                if (retired.IsAlive)
                    await Task.Delay(25, ct);
            }
            Ensure(!retired.IsAlive, "the real R1 service, Stub and Codec context is collectable while the peers remain active");
            Ensure((await CollectAsync(feed.Read(client, follow: false, ct)).WaitAsync(ct)).SequenceEqual(new[] { 20 }) &&
                   FeedCount(FeedType(newAssembly, "FeedReadingCodec"), "Serialized") == 2 &&
                   FeedCount(FeedType(clientAssembly, "FeedReadingCodec"), "Deserialized") == 4 &&
                   NStudyClientSession(client).CaptureNStudyBinding(identity.A) == a2.BindingId &&
                   await client.Get<IShutdownCleanupProbe>().TouchAsync(3, ct) == 103,
                "current R2 and B still respond on the original connection after the old context collects");
            trace.Record("OldCodecContextCollectsWhileR2AndBContinue", a2, bBinding);
        }
        finally
        {
            clientLoader.Unload();
            newLoader.Unload();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference Retired, NStudyAck Current, NStudyAck Unrelated)> ExerciseNStudyFeedReplacementAsync(
        SharpLinkServer server, SharpLinkClient client, string directory, string path, Assembly newAssembly,
        NStudyFeedClient feed, NStudyContractInputs identity, NStudyTrace trace, string? memoryName, CancellationToken ct)
    {
        var loader = new PluginLoadContext("feed-server-r1", directory);
        var weak = new WeakReference(loader);
        var oldAssembly = loader.LoadFromAssemblyPath(path);
        var service = FeedType(oldAssembly, "ReadingFeed");
        var oldCodec = FeedType(oldAssembly, "FeedReadingCodec");
        var newCodec = FeedType(newAssembly, "FeedReadingCodec");
        IAsyncEnumerator<int>? reader = null;
        try
        {
            Ensure(server.RegisterAssembly(oldAssembly).Succeeded, "server publishes the real R1 generated manifest");
            var oldModule = GetDynamicModule(server, oldAssembly);
            var originalRegistration = ServerRegistryTestAccessor.Services(server)[identity.A];
            Ensure(oldModule.Manifest.RpcAssemblyHash == identity.AHash &&
                   ReferenceEquals(originalRegistration.Stub.GetType().Assembly, oldAssembly),
                "R1 server has the same contract hash as the client and its own generated Stub");
            await server.StartAsync(ct);
            await client.ConnectAsync(ct);
            var a1 = await client.PrepareNStudyContractAsync(identity.A, identity.AHash,
                server.ReadNStudyTarget(oldAssembly, identity.A), ct);
            var bBinding = await client.PrepareNStudyContractAsync(identity.B, identity.BHash, default, ct);
            var session = NStudyClientSession(client);
            var peer = NStudyServerConnection(server);
            var b = client.Get<IShutdownCleanupProbe>();
            Ensure(peer.NStudyPhysicalIdentity == a1.Authority.PhysicalSession &&
                   peer.AuthenticationContext?.Subject == "n-validation" &&
                   (memoryName is null ? session.RemoteEndPoint is IPEndPoint : session.Input is SharedMemoryPipeReader),
                "normal generated calls are on the authenticated physical connection");

            reader = feed.Read(client, follow: true, ct).GetAsyncEnumerator(ct);
            Ensure(await reader.MoveNextAsync().AsTask().WaitAsync(ct) && reader.Current == 10,
                "R1 delivers its first reading through its own generated path");
            await ReadNStudyTask(service, "WaitingForUpdate").WaitAsync(ct);
            Ensure(FeedCount(oldCodec, "Serialized") == 1 && FeedCount(newCodec, "Serialized") == 0 &&
                   FeedCount(oldCodec, "Disposed") == 0 && oldModule.RemainingStreams == 1,
                "R1 serialization is observed before the cut and the old stream owns the module");
            trace.Record("R1FeedUsesItsOwnStubAndCodec", a1);

            var replacing = server.ReplaceAssemblyAsync(oldAssembly, newAssembly, TimeSpan.FromSeconds(15), ct).AsTask();
            await WaitForNStudyAsync(() => session.NStudyPreparedCount == 1, ct);
            var a2 = await client.PrepareNStudyContractAsync(identity.A, identity.AHash,
                server.ReadNStudyTarget(newAssembly, identity.A), ct);
            var current = ServerRegistryTestAccessor.Services(server)[identity.A];
            Ensure(a2.Authority == a1.Authority && a2.Target != a1.Target && a2.BindingId != a1.BindingId &&
                   ReferenceEquals(current.Stub.GetType().Assembly, newAssembly) &&
                   !ReferenceEquals(current.Stub, originalRegistration.Stub), "a distinct R2 Stub is current on the same Session");
            Ensure((await CollectAsync(feed.Read(client, follow: false, ct)).WaitAsync(ct)).SequenceEqual(new[] { 20 }) &&
                   FeedCount(newCodec, "Serialized") == 1 && FeedCount(oldCodec, "Serialized") == 1 &&
                   await b.TouchAsync(1, ct) == 101, "R2 uses a different Codec instance without blocking B or rerouting R1");
            await WaitForNStudyAsync(() => peer.NStudyBindings.Snapshot() == (2, 1, 1), ct);
            trace.Record("R2FeedAndBRunWhileR1Waits", a1, a2);

            service.GetMethod("PublishUpdate")!.Invoke(null, null);
            Ensure(await reader.MoveNextAsync().AsTask().WaitAsync(ct) && reader.Current == 11,
                "the old call emits another old reading after the new route has served a call");
            await ReadNStudyTask(service, "ClosingFeed").WaitAsync(ct);
            Ensure(FeedCount(oldCodec, "Serialized") == 2 && FeedCount(newCodec, "Serialized") == 1 &&
                   FeedCount(oldCodec, "Disposed") == 0 && FeedCount(service, "DisposedServices") == 0 &&
                   FeedCount(service, "ClosedFeeds") == 0 && !replacing.IsCompleted &&
                   oldModule.RemainingStreams == 1 && peer.NStudyBindings.Snapshot() == (2, 1, 1) &&
                   await b.TouchAsync(2, ct) == 102,
                "the old Codec performed the post-cut serialization and stays live during normal async cleanup");
            trace.Record("PostCutReadingAndAsyncCloseStillOwnR1Codec", a1, a2);

            service.GetMethod("FinishClosing")!.Invoke(null, null);
            Ensure(!await reader.MoveNextAsync().AsTask().WaitAsync(ct), "original feed reaches normal EOF");
            EnsureReplacementReleased(await replacing.WaitAsync(ct), "old feed owner finishes physically");
            await WaitForNStudyAsync(() => peer.NStudyBindings.Snapshot() == (2, 0, 0) && peer.ActiveCalls == 0 &&
                peer.Session.StreamManager.ActiveStreamCount == 0 && session.StreamManager.ActiveStreamCount == 0, ct);
            Ensure(FeedCount(service, "ClosedFeeds") == 1 && FeedCount(service, "DisposedServices") == 1 &&
                   FeedCount(oldCodec, "Disposed") == 1 && FeedCount(newCodec, "Disposed") == 0 &&
                   ReferenceEquals(NStudyClientSession(client), session), "R1 feed, service and codec close once; R2 stays live");
            trace.Record("NormalEofReleasesOnlyR1Owners", a1, a2);
            return (weak, a2, bBinding);
        }
        finally
        {
            service.GetMethod("PublishUpdate")!.Invoke(null, null);
            service.GetMethod("FinishClosing")!.Invoke(null, null);
            try
            {
                if (reader is not null)
                    await reader.DisposeAsync();
            }
            finally { loader.Unload(); }
        }
    }

    private static Type FeedType(Assembly assembly, string name)
        => assembly.GetType($"SharpLink.CodecPublicationDrainProvider.{name}", throwOnError: true)!;

    private static int FeedCount(Type type, string name) => (int)type.GetField(name)!.GetValue(null)!;

    private sealed class NStudyFeedClient(Type contract, Type reading)
    {
        private readonly MethodInfo _read = contract.GetMethod("ReadAsync")!;
        private readonly MethodInfo _project = typeof(RuntimeAssemblyIntegrationTests).GetMethod(nameof(ProjectFeed),
            BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(reading);

        internal IAsyncEnumerable<int> Read(SharpLinkClient client, bool follow, CancellationToken ct)
            => (IAsyncEnumerable<int>)_project.Invoke(null, [_read.Invoke(GetProxy(client, contract), [follow, ct]), ct])!;
    }

    private static async IAsyncEnumerable<int> ProjectFeed<T>(object source, [EnumeratorCancellation] CancellationToken ct)
    {
        var value = typeof(T).GetProperty("Value")!;
        await foreach (var reading in ((IAsyncEnumerable<T>)source).WithCancellation(ct))
            yield return (int)value.GetValue(reading)!;
    }
}
