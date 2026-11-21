using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public async Task NStudyPreparedOutputFreezesCallAndKeepsOriginalDecodeOwner(string transport)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = guard.Token;
        var trace = new NStudyTrace(transport);
        var clock = new SharpLink.UnitTests.Runtime.ManualTimeProvider();
        var directory = GetProjectOutputDirectory("SharpLink.CodecPublicationDrainProvider");
        var path = Path.Combine(directory, "SharpLink.CodecPublicationDrainProvider.dll");
        var serverLoader = new PluginLoadContext($"prepared-output-server-{transport}", directory);
        var newLoader = new PluginLoadContext($"prepared-output-r2-{transport}", directory);
        try
        {
            var serverAssembly = serverLoader.LoadFromAssemblyPath(path);
            var newAssembly = newLoader.LoadFromAssemblyPath(path);
            var service = FeedType(serverAssembly, "ReadingFeed");
            var staticLoad = SharpLinkAssemblyManifestLoader.TryLoad(typeof(IShutdownCleanupProbe).Assembly, out var staticManifest);
            var dynamicLoad = SharpLinkAssemblyManifestLoader.TryLoad(serverAssembly, out var dynamicManifest);
            Ensure(staticLoad.Succeeded && dynamicLoad.Succeeded, "real generated manifests load");
            var aCid = dynamicManifest!.Contracts.Single(c => c.ContractType == FeedType(serverAssembly, "IReadingFeed")).ContractId;
            var bCid = staticManifest!.Contracts.Single(c => c.ContractType == typeof(IShutdownCleanupProbe)).ContractId;
            var source = new FixedGeneratedManifestSource([staticManifest]);
            var observer = new NStudyPreparedOutputServerObserver(aCid);
            await using var provider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
            var builder = NewNStudyServer(source, provider).AddInterceptor(observer);
            var memory = transport == "shared-memory" ? $"sharplink-n-prepared-output-{Guid.NewGuid():N}" : null;
            if (memory is null)
                builder.UseTcp(0, IPAddress.Loopback);
            else
                builder.UseSharedMemory(memory);
            var port = memory is null ? ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port : 0;
            await using var server = (SharpLinkServer)builder.Build();
            await using var client = NewNStudyClient(source, port, memory, settings => settings.UseTimeProvider(clock));
            Ensure(server.RegisterAssembly(serverAssembly).Succeeded, "the actual feed service registers");
            await server.StartAsync(ct);
            await client.ConnectAsync(ct);
            var session = NStudyClientSession(client);
            var bBinding = await client.PrepareNStudyContractAsync(bCid, staticManifest.RpcAssemblyHash, default, ct);
            var (retired, a2) = await ExerciseNStudyPreparedOutputAsync(server, client, service, directory, path, newAssembly,
                clock, observer, trace, aCid, bBinding, memory, ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            for (var i = 0; retired.IsAlive && i < 24; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                if (retired.IsAlive)
                    await Task.Delay(25, ct);
            }
            Ensure(!retired.IsAlive && ReferenceEquals(NStudyClientSession(client), session) &&
                   session.CaptureNStudyBinding(aCid) == a2.BindingId &&
                   session.CaptureNStudyBinding(bCid) == bBinding.BindingId &&
                   await client.Get<IShutdownCleanupProbe>().TouchAsync(4, ct) == 104,
                "the old local output context collects without invalidating the current two routes");
            trace.Record("OldOutputContextCollectsWhileCurrentSessionContinues", a2, bBinding);
        }
        finally
        {
            newLoader.Unload();
            serverLoader.Unload();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference Old, NStudyAck Ready)> ExerciseNStudyPreparedOutputAsync(
        SharpLinkServer server, SharpLinkClient client, Type service, string directory, string path, Assembly newAssembly,
        SharpLink.UnitTests.Runtime.ManualTimeProvider clock, NStudyPreparedOutputServerObserver observer,
        NStudyTrace trace, long aCid, NStudyAck bBinding, string? memory, CancellationToken ct)
    {
        var loader = new PluginLoadContext("prepared-output-client-r1", directory);
        var retired = new WeakReference(loader);
        var oldAssembly = loader.LoadFromAssemblyPath(path);
        var oldCodec = FeedType(oldAssembly, "FeedReadingCodec");
        var newCodec = FeedType(newAssembly, "FeedReadingCodec");
        var pause = oldCodec.GetProperty("BeforeDecodeReturns")!;
        var decoding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseDecode = new ManualResetEventSlim();
        IAsyncEnumerator<int>? reader = null;
        try
        {
            pause.SetValue(null, (Action<int>)(value =>
            {
                if (value != 11)
                    return;
                decoding.TrySetResult();
                releaseDecode.Wait(ct);
            }));
            Ensure(client.RegisterAssembly(oldAssembly).Succeeded, "the first local feed generation registers");
            var oldModule = GetDynamicModule(client, oldAssembly);
            var generationOne = new NStudyPreparedOutputClientObserver(aCid);
            var generationTwo = new NStudyPreparedOutputClientObserver(aCid);
            client.ReplaceInterceptors([generationOne]);
            var original = BuildNStudyPreparedFeed(client, oldAssembly, "generated-output");
            var a1 = await original.Prepare(TimeSpan.FromSeconds(5), ct);
            var connection = NStudyClientConnection(client);
            var session = connection.Session;
            var peer = NStudyServerConnection(server);
            var b = client.Get<IShutdownCleanupProbe>();
            Ensure(a1.Authority.PhysicalSession == peer.NStudyPhysicalIdentity &&
                   peer.AuthenticationContext?.Subject == "n-validation" &&
                   (memory is null ? session.RemoteEndPoint is IPEndPoint : session.Input is SharedMemoryPipeReader),
                "the prepared stream runs on the expected authenticated transport");
            var bindSequence = session.NStudyBindOperationForDiagnostics;

            var deferred = original.Generated(true, ct);
            reader = deferred.GetAsyncEnumerator(ct);
            client.ReplaceInterceptors([generationTwo]);
            clock.Advance(TimeSpan.FromSeconds(2));
            Ensure(await b.TouchAsync(1, ct) == 101 && generationOne.Calls == 0 && generationTwo.Calls == 0 &&
                   oldModule.RemainingStreams == 0 && observer.Calls.IsEmpty && connection.PendingCalls.ActiveCount == 0,
                "construction and GetAsyncEnumerator do not acquire or send; the ordinary next caller policy may already change");
            trace.Record("StreamCreationFreezesPolicyWithoutAcquiring", a1, bBinding);

            Ensure(await reader.MoveNextAsync().AsTask().WaitAsync(ct) && reader.Current == 10,
                "first enumeration acquires and receives the normal generated item");
            await ReadNStudyTask(service, "WaitingForUpdate").WaitAsync(ct);
            var first = observer.Calls.Single();
            var originalToken = generationOne.LastToken;
            var sourceHandle = originalToken.WaitHandle.SafeWaitHandle;
            Ensure(oldModule.RemainingStreams == 1 && generationOne.Calls == 1 && generationTwo.Calls == 0 &&
                   first.Label == "generated-output" && first.Remaining > TimeSpan.Zero &&
                   first.Remaining <= TimeSpan.FromSeconds(10) && first.Method.MethodTimeout == TimeSpan.FromSeconds(12) &&
                   FeedCount(oldCodec, "Deserialized") == 1,
                "the first enumeration uses the call-time interceptor and the original elapsed 12 s method deadline");
            Ensure((await CollectAsync(original.Manual(false, Metadata("manual-output"), ct)).WaitAsync(ct)).SequenceEqual(new[] { 10 }) &&
                   observer.Calls.Last().Label == "manual-output" && generationTwo.Calls == 1 &&
                   FeedCount(oldCodec, "Deserialized") == 2 && session.NStudyBindOperationForDiagnostics == bindSequence &&
                   session.CaptureNStudyBinding(aCid) == a1.BindingId,
                "the manual stream consumes the same prepared receiver and codec without a second Bind");
            trace.Record("FirstEnumerationUsesFrozenControlsAndSharedCodec", a1);

            var replacing = client.ReplaceAssemblyAsync(oldAssembly, newAssembly, TimeSpan.FromSeconds(15), ct).AsTask();
            var current = BuildNStudyPreparedFeed(client, newAssembly, "current-output");
            var a2 = await current.Prepare(TimeSpan.FromSeconds(5), ct);
            Ensure((await CollectAsync(current.Manual(false, Metadata("manual-r2-output"), ct)).WaitAsync(ct)).SequenceEqual(new[] { 10 }) &&
                   FeedCount(newCodec, "Deserialized") == 1 && FeedCount(oldCodec, "Disposed") == 0 &&
                   oldModule.RemainingStreams == 1 && !replacing.IsCompleted && a1.Authority == a2.Authority &&
                   a1.Target == a2.Target && a1.BindingId != a2.BindingId && await b.TouchAsync(2, ct) == 102,
                "the new local generation and B can use the same Session while the old output is active");
            trace.Record("NewSiteAndBRunWhileOldOutputWaits", a1, a2);

            var next = reader.MoveNextAsync().AsTask();
            service.GetMethod("PublishUpdate")!.Invoke(null, null);
            await decoding.Task.WaitAsync(ct);
            await ReadNStudyTask(service, "ClosingFeed").WaitAsync(ct);
            clock.Advance(TimeSpan.FromSeconds(10));
            try
            {
                await next.WaitAsync(ct);
                throw new InvalidOperationException("The stream's normal logical deadline should have elapsed.");
            }
            catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.DeadlineExceeded) { }
            Ensure(!replacing.IsCompleted && oldModule.RemainingStreams == 1 && !sourceHandle.IsClosed &&
                   FeedCount(oldCodec, "Disposed") == 0 && FeedCount(oldCodec, "Deserialized") == 2 &&
                   FeedCount(newCodec, "Deserialized") == 1,
                "the caller sees a deadline while the original physical output decoder, codec and CTS remain live");
            trace.Record("LogicalDeadlineDoesNotReleaseOriginalDecoder", a1, a2);

            releaseDecode.Set();
            service.GetMethod("FinishClosing")!.Invoke(null, null);
            EnsureReplacementReleased(await replacing.WaitAsync(ct), "the old client output drains physically");
            await WaitForNStudyAsync(() => connection.ActiveCallCount == 0 && peer.ActiveCalls == 0 &&
                peer.NStudyBindings.Snapshot() == (2, 0, 0) && session.StreamManager.ActiveStreamCount == 0 &&
                peer.Session.StreamManager.ActiveStreamCount == 0 && FeedCount(service, "ClosedFeeds") == 3, ct);
            Ensure(FeedCount(oldCodec, "Deserialized") == 3 && FeedCount(oldCodec, "Disposed") == 1 &&
                   FeedCount(newCodec, "Disposed") == 0 && sourceHandle.IsClosed &&
                   ReferenceEquals(NStudyClientSession(client), session) && await b.TouchAsync(3, ct) == 103,
                "the original item finishes decoding after logical expiration, then only the old client owners are reclaimed");
            trace.Record("PhysicalDecodeExitClosesOnlyOriginalOwners", a1, a2);
            return (retired, a2);
        }
        finally
        {
            releaseDecode.Set();
            pause.SetValue(null, null);
            service.GetMethod("PublishUpdate")!.Invoke(null, null);
            service.GetMethod("FinishClosing")!.Invoke(null, null);
            try { if (reader is not null) await reader.DisposeAsync(); }
            finally { loader.Unload(); }
        }
    }

    private static NStudyPreparedFeed BuildNStudyPreparedFeed(SharpLinkClient client, Assembly assembly, string label)
        => (NStudyPreparedFeed)typeof(RuntimeAssemblyIntegrationTests).GetMethod(nameof(BuildNStudyTypedFeed),
            BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(FeedType(assembly, "IReadingFeed"), FeedType(assembly, "FeedReading"))
            .Invoke(null, [client, assembly, label])!;

    private static NStudyPreparedFeed BuildNStudyTypedFeed<TContract, TReading>(
        SharpLinkClient client, Assembly assembly, string label) where TContract : IService
    {
        var site = CreateNStudyPublicStreaming<TContract>(client, assembly, label);
        var method = site.Method("ReadAsync");
        var proxyRead = typeof(TContract).GetMethod("ReadAsync")!;
        var requestCodec = site.Codecs.GetCodec<bool>();
        var responseCodec = site.Codecs.GetCodec<TReading>();
        // Inventory tests still use the private observation contract until the public receipt API exists.
        var exact = (SharpLinkClient.NStudyContractSite)site.Channel;
        return new(site.Prepare, exact.PrepareExactAsync,
            (follow, ct) => ProjectFeed<TReading>(proxyRead.Invoke(site.Service, [follow, ct])!, ct),
            (follow, metadata, ct) => ProjectFeed<TReading>(site.Channel.InvokeServerStreamingAsync(method,
                follow, requestCodec, responseCodec, metadata, ct), ct));
    }

    private sealed record NStudyPreparedFeed(
        Func<TimeSpan, CancellationToken, ValueTask<NStudyAck>> Prepare,
        Func<NStudyObservedTarget, TimeSpan, CancellationToken, ValueTask<NStudyAck>> PrepareExact,
        Func<bool, CancellationToken, IAsyncEnumerable<int>> Generated,
        Func<bool, SharpLinkMetadata, CancellationToken, IAsyncEnumerable<int>> Manual);

    private sealed class NStudyPreparedOutputServerObserver(long contract) : ISharpLinkServerInterceptor
    {
        private static readonly PropertyInfo Deadline = typeof(SharpLinkCallContextSnapshot)
            .GetProperty("LocalRpcDeadline", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly MethodInfo GetRemaining = Deadline.PropertyType.GetMethod("GetRemaining",
            BindingFlags.Instance | BindingFlags.NonPublic, [typeof(TimeProvider)])!;
        internal ConcurrentQueue<(string? Label, RpcMethodDescriptor Method, TimeSpan Remaining)> Calls { get; } = new();
        public ValueTask InvokeAsync(SharpLinkServerInvocationContext context, SharpLinkServerInvocationDelegate next)
        {
            if (context.Method.ContractId == contract)
                Calls.Enqueue((context.Metadata?.Single(item => item.Key == "caller").Value, context.Method,
                    (TimeSpan)GetRemaining.Invoke(Deadline.GetValue(context), [TimeProvider.System])!));
            return next(context);
        }
    }

    private sealed class NStudyPreparedOutputClientObserver(long contract) : ISharpLinkClientInterceptor
    {
        internal int Calls;
        internal CancellationToken LastToken { get; private set; }
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            if (context.Method.ContractId == contract)
            {
                LastToken = context.CancellationToken;
                Interlocked.Increment(ref Calls);
            }
            return next(context);
        }
    }
}
