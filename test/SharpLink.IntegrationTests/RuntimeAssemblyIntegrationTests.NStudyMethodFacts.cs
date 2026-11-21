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
    public async Task NStudyGeneratedFactsSurviveQueueDecodeAndOutputMappingWithoutStubMetadataReads(string transport)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = guard.Token;
        var trace = new NStudyTrace(transport);
        var load = SharpLinkAssemblyManifestLoader.TryLoad(typeof(INStudyFactsProbe).Assembly, out var manifest);
        Ensure(load.Succeeded, "the real generated fixture manifest loads");
        var aContract = manifest!.Contracts.Single(item => item.ContractType == typeof(INStudyFactsProbe));
        var aCid = aContract.ContractId;
        var bCid = manifest.Contracts.Single(item => item.ContractType == typeof(IShutdownCleanupProbe)).ContractId;
        var methodId = aContract.Methods.Single(item => item.Name == nameof(INStudyFactsProbe.MeasureAsync)).MethodId;
        var outputId = aContract.Methods.Single(item => item.Name == nameof(INStudyFactsProbe.ReadSamplesAsync)).MethodId;
        var count = new NStudyStubMetadataCounts();
        var wrap = new NStudyCountingManifest(manifest, aCid, count);
        var clientSource = new FixedGeneratedManifestSource([manifest]);
        var serverSource = new FixedGeneratedManifestSource([wrap]);
        var receiver = new NStudyCountingCompression();
        var sender = new NStudyCountingCompression();
        var probe = new NStudyFactsProbe();
        var recorder = new NStudyFactsInterceptor();
        var mapper = new NStudyFactsMapper();
        await using var provider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
        var builder = NewNStudyServer(serverSource, provider).ReplaceService<INStudyFactsProbe>(probe)
            .UseRuntime(options => options.Compression.Providers.Add(receiver))
            .AddInterceptor(recorder).UseExceptionMapper(mapper)
            .UseAdmissionControl(options =>
            {
                options.AddContract(aCid, rule => rule.UseConcurrency(1));
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(15);
            });
        var memoryName = transport == "shared-memory" ? $"sharplink-n-facts-{Guid.NewGuid():N}" : null;
        if (memoryName is null)
            builder.UseTcp(0, IPAddress.Loopback);
        else
            builder.UseSharedMemory(memoryName);
        var port = memoryName is null ? ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port : 0;
        await using var server = (SharpLinkServer)builder.Build();
        await using var client = NewNStudyClient(clientSource, port, memoryName,
            settings => settings.UseRuntime(options => options.Compression.Providers.Add(sender)));
        var cold = count.PreparedCount;
        Ensure(cold == aContract.Methods.Count &&
               !RuntimeHelpers.IsReferenceOrContainsReferences<NStudyMethodFacts>(),
            "each registered method is prepared once; the queued facts and executor slot hold no object reference");
        try
        {
            await server.StartAsync(ct);
            await client.ConnectAsync(ct);
            var aProxy = client.CreateProxy<INStudyFactsProbe>();
            var bProxy = client.CreateProxy<IShutdownCleanupProbe>();
            await aProxy.PrepareAsync(TimeSpan.FromSeconds(5), ct);
            await bProxy.PrepareAsync(TimeSpan.FromSeconds(5), ct);
            var session = NStudyClientSession(client);
            var aBinding = session.CaptureNStudyRoute(aCid).Acknowledgement;
            var bBinding = session.CaptureNStudyRoute(bCid).Acknowledgement;
            var peer = NStudyServerConnection(server);
            Ensure(peer.NStudyPhysicalIdentity == aBinding.Authority.PhysicalSession &&
                   peer.AuthenticationContext?.Subject == "n-validation" &&
                   (memoryName is null ? session.RemoteEndPoint is IPEndPoint : session.Input is SharedMemoryPipeReader),
                "real Session and generated contract are authenticated");
            trace.Record("GeneratedEntriesPreparedOnlyDuringRegistration", aBinding, bBinding);
            var a = aProxy.Service;
            var b = bProxy.Service;
            var holding = a.OccupyAsync(ct).AsTask();
            await probe.Started.WaitAsync(ct);
            var batch = Enumerable.Repeat((byte)17, 1024 * 1024 + 64).ToArray();
            var measure = a.MeasureAsync(batch, ct).AsTask();
            var kernel = server.CurrentAdmissionProgramForTests!.Kernel;
            await WaitForNStudyAsync(() => kernel.QueuedCalls == 1, ct);
            Ensure(receiver.Decoded == 0 && sender.Compressed > 0 && count.PreparedCount == cold &&
                   count.Invocations(methodId) == 0 && !recorder.Methods.ContainsKey(methodId) &&
                   await b.TouchAsync(1, ct) == 101,
                "actual compressed business goes to admission before provider decode and interception, with no Stub metadata access");
            trace.Record("QueuedCompressedHeadUsesPrecomputedFactsBeforeDecode", aBinding);

            probe.Release();
            Ensure(await holding.WaitAsync(ct) == 42 && await measure.WaitAsync(ct) == batch.Length + 17,
                "normal offloaded decode invokes the typed stub with unchanged business data");
            Ensure(server.DecodeStartedWorkCountForDiagnostics == 1 && receiver.Decoded == 1 &&
                   recorder.Methods.TryGetValue(methodId, out var measured) && measured.ContractId == aCid &&
                   measured.Kind == RpcMethodKind.Unary && measured.IsIdempotent && measured.HasMethodTimeout &&
                   measured.MethodTimeout == TimeSpan.FromSeconds(15) && measured.SupportsCancellation &&
                   measured == aContract.Methods.Single(item => item.MethodId == methodId).Invocation &&
                   count.PreparedCount == cold &&
                   count.Invocations(methodId) == 1,
                "one real persistent decode and the same generated method facts reach the server interceptor");
            trace.Record("PersistentWorkerAndInterceptorReuseExactMethodFacts", aBinding);

            // Exercise ordinary application error mapping without interceptor
            // context. The original streaming call supplies its own method facts.
            server.ReplaceInterceptors([]);
            Ensure(await a.MeasureAsync([17, 18], ct) == 19 && count.Invocations(methodId) == 2 &&
                   count.PreparedCount == cold && recorder.Visits(methodId) == 1,
                "direct and intercepted calls reach the same prepared method without a legacy ID dispatch");
            trace.Record("DirectAndInterceptedCallsUseOnePreparedEntry", aBinding);
            await using var samples = a.ReadSamplesAsync(ct).GetAsyncEnumerator(ct);
            Ensure(await samples.MoveNextAsync().AsTask().WaitAsync(ct) && samples.Current == 7,
                "normal generated output arrives before the application's unavailable sample");
            try
            {
                await samples.MoveNextAsync().AsTask().WaitAsync(ct);
                throw new InvalidOperationException("Expected the service's normal business error.");
            }
            catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.FailedPrecondition)
            {
                Ensure(error.Message == "Samples are unavailable.", "wire mapping preserves the configured business status");
            }
            await WaitForNStudyAsync(() => peer.NStudyBindings.Snapshot() == (2, 0, 0), ct);
            Ensure(mapper.Method is { } output && output.ContractId == aCid && output.MethodId == outputId &&
                   output.Kind == RpcMethodKind.ServerStreaming && output.SupportsCancellation &&
                   output == aContract.Methods.Single(item => item.MethodId == outputId).Invocation &&
                   count.PreparedCount == cold &&
                   count.Invocations(outputId) == 1 && !recorder.Methods.ContainsKey(outputId) &&
                   await b.TouchAsync(2, ct) == 102,
                "output mapper keeps the actual original descriptor with no interceptor and no Stub metadata re-read");
            trace.Record("OutputErrorMapsOriginalFactsWithoutInterceptorOrStubRead", aBinding, bBinding);
        }
        finally { probe.Release(); }
    }

    private sealed class NStudyFactsInterceptor : ISharpLinkServerInterceptor
    {
        internal ConcurrentDictionary<long, RpcMethodDescriptor> Methods { get; } = new();
        private readonly ConcurrentDictionary<long, int> _visits = new();
        internal int Visits(long method) => _visits.GetValueOrDefault(method);
        public ValueTask InvokeAsync(SharpLinkServerInvocationContext context, SharpLinkServerInvocationDelegate next)
        {
            Methods[context.Method.MethodId] = context.Method;
            _visits.AddOrUpdate(context.Method.MethodId, 1, static (_, count) => count + 1);
            return next(context);
        }
    }

    private sealed class NStudyFactsMapper : IRpcExceptionMapper
    {
        internal RpcMethodDescriptor? Method { get; private set; }
        public SharpLinkException Map(Exception exception, SharpLinkServerInvocationContext context)
        {
            Method = context.Method;
            return new(SharpLinkErrorCode.FailedPrecondition, "Samples are unavailable.", exception);
        }
    }

    private sealed class NStudyStubMetadataCounts
    {
        internal int Prepared;
        internal readonly ConcurrentDictionary<long, int> Executed = new();
        internal int Invocations(long method) => Executed.GetValueOrDefault(method);
        internal int PreparedCount => Volatile.Read(ref Prepared);
    }

    private sealed class NStudyCountingStub(IRpcStub inner, NStudyStubMetadataCounts count)
        : IRpcStub
    {
        public long InterfaceHash => inner.InterfaceHash;
        public bool TryPrepareMethod(long methodId, out RpcGeneratedPreparedMethod method)
        {
            Interlocked.Increment(ref count.Prepared);
            if (!inner.TryPrepareMethod(methodId, out method))
                return false;
            var execute = method.Execute;
            var declared = method.Descriptor;
            method = method with
            {
                Execute = (object service, IRpcGeneratedServerBridge bridge, in RpcMethodDescriptor facts,
                    long request, ReadOnlySequence<byte> arguments, IBufferWriter<byte>? output, CancellationToken cancellationToken) =>
                {
                    Ensure(facts == declared, "the generated executor receives the original complete descriptor");
                    count.Executed.AddOrUpdate(methodId, 1, static (_, calls) => calls + 1);
                    return execute(service, bridge, in facts, request, arguments, output, cancellationToken);
                }
            };
            return true;
        }
    }

    private sealed class NStudyCountingManifest(
        ISharpLinkGeneratedAssemblyManifest original, long contract, NStudyStubMetadataCounts count)
        : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => original.ApiVersion;
        public int ProtocolVersion => original.ProtocolVersion;
        public string GeneratorVersion => original.GeneratorVersion;
        public Assembly OwnerAssembly => original.OwnerAssembly;
        public RpcHash128 RpcAssemblyHash => original.RpcAssemblyHash;
        public string CompileTimeDescriptor => original.CompileTimeDescriptor;
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; } = original.Contracts.Select(item =>
            item.ContractId == contract ? item with
            {
                StubFactory = codecs => new NStudyCountingStub(item.StubFactory(codecs), count)
            } : item).ToArray();
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => original.Services;
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => original.Codecs;
        public IReadOnlyList<IRpcGeneratedCodecFactory> ContractCodecs => original.ContractCodecs;
        public IReadOnlyList<string> Dependencies => original.Dependencies;
        public IReadOnlyList<string> ContractDependencies => original.ContractDependencies;
    }
}

[RpcContract]
public interface INStudyFactsProbe : IService
{
    ValueTask<int> OccupyAsync(CancellationToken cancellationToken);

    [Idempotent]
    [SharpLink.Sdk.Timeout(15)]
    ValueTask<int> MeasureAsync(byte[] batch, CancellationToken cancellationToken);

    IAsyncEnumerable<int> ReadSamplesAsync(CancellationToken cancellationToken);
}

[RpcService]
public sealed class NStudyFactsProbe : INStudyFactsProbe
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Started => _entered.Task;
    internal void Release() => _release.TrySetResult();

    public async ValueTask<int> OccupyAsync(CancellationToken cancellationToken)
    {
        _entered.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken);
        return 42;
    }

    public ValueTask<int> MeasureAsync(byte[] batch, CancellationToken cancellationToken)
        => ValueTask.FromResult(batch.Length + batch[0]);

    public async IAsyncEnumerable<int> ReadSamplesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return 7;
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Sample store is unavailable.");
    }
}
