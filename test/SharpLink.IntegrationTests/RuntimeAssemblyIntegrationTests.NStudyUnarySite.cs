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
    public async Task NStudyGeneratedAndManualUnaryUseTheSamePreparedSite(string transport)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = guard.Token;
        var trace = new NStudyTrace(transport);
        var loaded = SharpLinkAssemblyManifestLoader.TryLoad(typeof(INStudyUnarySiteProbe).Assembly, out var manifest);
        Ensure(loaded.Succeeded, "the actual generated manifest loads");
        var contract = manifest!.Contracts.Single(c => c.ContractType == typeof(INStudyUnarySiteProbe));
        var method = contract.Methods.Single();
        var bCid = manifest.Contracts.Single(c => c.ContractType == typeof(IShutdownCleanupProbe)).ContractId;
        var count = new NStudyStubMetadataCounts();
        var recorder = new NStudyUnarySiteObserver(contract.ContractId);
        var clientObserver = new NStudyUnarySiteClientObserver(contract.ContractId);
        var clientSource = new FixedGeneratedManifestSource([manifest]);
        var serverSource = new FixedGeneratedManifestSource([new NStudyCountingManifest(manifest, contract.ContractId, count)]);
        await using var provider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
        var builder = NewNStudyServer(serverSource, provider)
            .ReplaceService<INStudyUnarySiteProbe>(new NStudyUnarySiteProbe()).AddInterceptor(recorder);
        var memory = transport == "shared-memory" ? $"sharplink-n-unary-site-{Guid.NewGuid():N}" : null;
        if (memory is null)
            builder.UseTcp(0, IPAddress.Loopback);
        else
            builder.UseSharedMemory(memory);
        var port = memory is null ? ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port : 0;
        await using var server = (SharpLinkServer)builder.Build();
        await using var client = NewNStudyClient(clientSource, port, memory, settings => settings.AddInterceptor(clientObserver));
        var site = client.CreateNStudyUnarySite<INStudyUnarySiteProbe>(Metadata("generated"));
        var manual = site.BindUnary(method.MethodId);
        var requestCodec = new NStudyManualIntRequestCodec();
        var responseCodec = site.GetCodec<int>();
        Ensure(ReferenceEquals(site.RuntimeContext, client.RuntimeContext) && client.ReadyConnectionCount == 0 &&
               client.PendingCallCount == 0 && count.Snapshot() == (1, 0),
            "both local entries can be created before connect without a business call or second server preparation");
        trace.Record("GeneratedAndManualEntriesAreLocalBeforeConnect");

        await server.StartAsync(ct);
        await client.ConnectAsync(ct);
        var session = NStudyClientSession(client);
        var peer = NStudyServerConnection(server);
        Ensure(session.NStudyPreparedCount == 0, "connection alone does not prepare any business route");
        var first = await site.PrepareAsync(TimeSpan.FromSeconds(5), ct);
        var same = await site.PrepareAsync(TimeSpan.FromSeconds(5), ct);
        var bBinding = await client.PrepareNStudyContractAsync(bCid, manifest.RpcAssemblyHash, default, ct);
        Ensure(first == same && first.Authority.PhysicalSession == peer.NStudyPhysicalIdentity &&
               session.CaptureNStudyBinding(contract.ContractId) == first.BindingId &&
               peer.AuthenticationContext?.Subject == "n-validation" &&
               (memory is null ? session.RemoteEndPoint is IPEndPoint : session.Input is SharedMemoryPipeReader) &&
               peer.NStudyBindings.Snapshot() == (2, 0, 0) && count.Invocations(method.MethodId) == 0,
            "explicit readiness installs one route for this site; a ready Prepare does not Bind again");
        trace.Record("OnePreparedSiteOnTheAuthenticatedSession", first, bBinding);

        Ensure(await site.Service.ReportAsync(5, ct) == 105 &&
               await manual.InvokeAsync(9, requestCodec, responseCodec, Metadata("manual"), ct) == 109,
            "a generated payload and a handwritten typed payload reach the same business method");
        var originals = recorder.Calls.ToArray();
        Ensure(originals.Length == 2 && originals[0].Label == "generated" && originals[1].Label == "manual" &&
               originals.All(call => call.Method == method.Invocation && call.HasDeadline) &&
               originals[0].Connection == originals[1].Connection && clientObserver.Calls == 2 &&
               requestCodec.Writes == 1 && count.Invocations(method.MethodId) == 2 && count.Snapshot() == (1, 0) &&
               session.CaptureNStudyBinding(contract.ContractId) == first.BindingId,
            "both share one Session route, server executor, client interceptor pipeline and generated method budget; metadata stays distinct");
        trace.Record("BothCallersReuseOneBindingAndServerEntry", first);

        var legacy = client.GetWithMetadata<INStudyUnarySiteProbe>(Metadata("legacy"));
        Ensure(await legacy.ReportAsync(7, ct) == 107 && recorder.Calls.Last().Label == "legacy" &&
               await client.Get<IShutdownCleanupProbe>().TouchAsync(1, ct) == 101,
            "the original entry and unrelated B remain usable on the same client");
        trace.Record("ExistingMetadataEntryAndUnrelatedContractStillWork", first, bBinding);

        Ensure(await client.ReleaseNStudyContractAsync(contract.ContractId, ct),
            "the application can release its route in the ordinary way");
        var second = await site.PrepareAsync(TimeSpan.FromSeconds(5), ct);
        Ensure(second.BindingId != first.BindingId && second.Authority == first.Authority &&
               second.Target == first.Target && ReferenceEquals(NStudyClientSession(client), session) &&
               await site.Service.ReportAsync(12, ct) == 112 &&
               await manual.InvokeAsync(14, requestCodec, responseCodec, Metadata("manual-again"), ct) == 114,
            "both retained callers use a new exact Bind after explicit re-Prepare on the original Session");
        await WaitForNStudyAsync(() => peer.NStudyBindings.Snapshot() == (2, 0, 0) && client.PendingCallCount == 0, ct);
        Ensure(recorder.Calls.Last().Label == "manual-again" && clientObserver.Calls == 5 &&
               requestCodec.Writes == 2 && count.Invocations(method.MethodId) == 5 && count.Snapshot() == (1, 0) &&
               session.CaptureNStudyBinding(bCid) == bBinding.BindingId,
            "the same executor receives all five calls, and physical uses return to baseline");
        trace.Record("ExplicitReprepareRefreshesBothCallersWithoutReconnecting", first, second);
    }

    private static SharpLinkMetadata Metadata(string label) => new(new KeyValuePair<string, string>("caller", label));

    private sealed class NStudyUnarySiteObserver(long contract) : ISharpLinkServerInterceptor
    {
        private static readonly PropertyInfo LocalDeadline = typeof(SharpLinkCallContextSnapshot)
            .GetProperty("LocalRpcDeadline", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly PropertyInfo HasDeadline = LocalDeadline.PropertyType
            .GetProperty("HasValue", BindingFlags.Instance | BindingFlags.NonPublic)!;

        internal ConcurrentQueue<(string? Label, RpcMethodDescriptor Method, string Connection, bool HasDeadline)> Calls { get; } = new();
        public ValueTask InvokeAsync(SharpLinkServerInvocationContext context, SharpLinkServerInvocationDelegate next)
        {
            if (context.Method.ContractId == contract)
                Calls.Enqueue((context.Metadata?.Single(item => item.Key == "caller").Value, context.Method,
                    context.ConnectionId, (bool)HasDeadline.GetValue(LocalDeadline.GetValue(context))!));
            return next(context);
        }
    }

    private sealed class NStudyUnarySiteClientObserver(long contract) : ISharpLinkClientInterceptor
    {
        internal int Calls;
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            if (context.Method.ContractId == contract)
                Interlocked.Increment(ref Calls);
            return next(context);
        }
    }

    private sealed class NStudyManualIntRequestCodec : IRpcCodec<int>
    {
        internal int Writes;
        public void Serialize(in int value, IBufferWriter<byte> buffer)
        {
            Interlocked.Increment(ref Writes);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.GetSpan(sizeof(int)), value);
            buffer.Advance(sizeof(int));
        }
        public int Deserialize(in ReadOnlySequence<byte> buffer)
            => throw new NotSupportedException("The handwritten request codec is used only for writing.");
    }
}

[RpcContract]
public interface INStudyUnarySiteProbe : IService
{
    [SharpLink.Sdk.Timeout(12)]
    ValueTask<int> ReportAsync(int value, CancellationToken cancellationToken);
}

[RpcService]
public sealed class NStudyUnarySiteProbe : INStudyUnarySiteProbe
{
    public ValueTask<int> ReportAsync(int value, CancellationToken cancellationToken)
        => ValueTask.FromResult(value + 100);
}
