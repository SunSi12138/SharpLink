using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.Client;
using SharpLink.Server;

namespace SharpLink.BindingSlice;

internal static partial class Program
{
    private static async Task RunFactsAsync(string transport, CancellationToken token)
    {
        Check(Unsafe.SizeOf<RpcMethodDescriptor>() == 32, "facts descriptor retains 32 byte layout");
        Check(!RuntimeHelpers.IsReferenceOrContainsReferences<RpcMethodDescriptor>(), "facts descriptor contains no collectible reference");
        Check(typeof(IRpcStub).GetMethod("TryGetMethodDescriptor") is null && typeof(IRpcStub).GetMethod("SupportsCancellation") is null &&
            typeof(IRpcStub).GetMethod("TryResolveMethod") is { IsAbstract: true }, "facts Stub requires one resolver without legacy default fallback");
        Check(typeof(IRpcStub).GetMethods().Where(m => m.Name.StartsWith("Invoke", StringComparison.Ordinal)).Count(m =>
            m.GetParameters()[2].ParameterType == typeof(RpcMethodDescriptor).MakeByRefType() && m.GetParameters()[2].IsIn) == 4,
            "facts all four Stub invocations consume readonly resolved descriptor");
        var ticksPreserved = true;
        foreach (var ticks in new[] { long.MinValue, -1L, 0L, 1L, 123456789L, long.MaxValue })
        {
            var m = new RpcMethodDescriptor(-17, long.MaxValue, RpcMethodKind.Unary, true, false, true,
                TimeSpan.FromTicks(ticks), true, 0, true) { SupportsCancellation = true };
            ticksPreserved &= m.MethodTimeout!.Value.Ticks == ticks && m.HasMethodTimeout && m.IsIdempotent && m.ResponseNullable &&
                m.SupportsCancellation && m.HasResponsePayload && m.ContractId == -17 && m.MethodId == long.MaxValue;
        }
        Check(ticksPreserved, "facts all flags and signed full timeout ticks survive together");
        var absent = new RpcMethodDescriptor(1, 2, RpcMethodKind.Unary, false, false, false, null);
        Check(absent.MethodTimeout is null && !absent.HasMethodTimeout &&
            (absent with { HasMethodTimeout = true }).MethodTimeout is null &&
            (absent with { MethodTimeout = TimeSpan.Zero }).MethodTimeout == TimeSpan.Zero,
            "facts timeout declaration null and zero remain distinct");
        Check(ResolvedMethodValidation.IsValid(new(1, 2, RpcMethodKind.ClientStreaming, true, true, false, null, ClientStreamCount: 127)
            { SupportsCancellation = true }, 1, 2), "facts client stream count 127 is not narrowed");
        Check(!ResolvedMethodValidation.IsValid(new(1, 2, RpcMethodKind.ClientStreaming, true, true, false, null, ClientStreamCount: 128)
            { SupportsCancellation = true }, 1, 2), "facts client stream count 128 fails closed");
        Check(!ResolvedMethodValidation.IsValid(absent with { Kind = (RpcMethodKind)255 }, 1, 2) &&
            !ResolvedMethodValidation.IsValid(absent with { HasClientStreams = true }, 1, 2) &&
            !ResolvedMethodValidation.IsValid(absent with { Kind = RpcMethodKind.OneWay, HasClientStreams = true, ClientStreamCount = 1 }, 1, 2),
            "facts invalid kind inconsistent stream shape and missing cancellation are rejected");
        WrongFactsAbiPreflight();

        await using var f = new FactsFixture(transport);
        foreach (var kind in new[] { RpcMethodKind.Unary, RpcMethodKind.OneWay, RpcMethodKind.ClientStreaming, RpcMethodKind.ServerStreaming, RpcMethodKind.DuplexStreaming })
        {
            var methods = f.Contract.Methods.Where(m => m.Kind == kind).ToArray();
            Check(methods.Length != 0 && methods.All(m => f.Stub.Inner.TryResolveMethod(m.MethodId, out var result) &&
                result == m.Facts && result.Kind == m.Kind && result.SupportsCancellation == m.SupportsCancellation &&
                ResolvedMethodValidation.IsValid(result, f.Contract.ContractId, m.MethodId)), "facts generated " + kind + " keeps complete manifest value");
        }
        Check(f.Method(nameof(IFactsRpc.TickAsync)).MethodTimeout?.Ticks == 1 && f.Method(nameof(IFactsRpc.TickAsync)).IsIdempotent &&
            f.Method(nameof(IFactsRpc.DeclaredAsync)).HasMethodTimeout && f.Method(nameof(IFactsRpc.DeclaredAsync)).MethodTimeout is null &&
            !f.Method(nameof(IFactsRpc.PlainAsync)).HasMethodTimeout, "facts generated explicit tick default timeout and absent timeout differ");
        Check(f.Stub.Inner.TryResolveMethod(f.Method(nameof(IFactsRpc.NullableAsync)).MethodId, out var nullable) && nullable.ResponseNullable,
            "facts generated nullable response is preserved");
        Check(!f.Stub.Inner.TryResolveMethod(long.MaxValue, out var unknown) && unknown == default,
            "facts unknown resolver result remains unknown rather than fabricated Unary");
        await DirectGeneratedFactsAsync(f, token);
        var registration = Field<ServerServiceModuleRegistry>(f.Server, "_serviceModuleRegistry").ServicesStorage[f.Contract.ContractId];
        var quota = new StaticBindingBudget(); var table = new StaticBindingTable(quota);
        f.Stub.MutateResult = true;
        Check(table.Install(f.Contract.ContractId, registration, f.Contract, 0).Failure == StaticBindingFailure.UnsupportedShape &&
            table.Count == 0 && quota.Bindings == 0, "facts complete resolver mismatch rejects before entry and quota publication");
        f.Stub.MutateResult = false; f.Stub.Resolutions = 0; table.Close();

        var serverActivities = new ConcurrentQueue<(long Contract, long Method)>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "SharpLink.Server",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { if (activity.GetTagItem("rpc.sharplink.contract_id") is long c && activity.GetTagItem("rpc.sharplink.method_id") is long m) serverActivities.Enqueue((c, m)); }
        };
        ActivitySource.AddActivityListener(listener);
        var rpc = f.Client.CreateProxy<IFactsRpc>();
        var site = (StaticBindingCallSite)f.Client.CreateContractChannel(f.Client.GetLocalContract<IFactsRpc>());
        await f.Server.StartAsync(token); await f.Client.ConnectAsync(token); await rpc.PrepareAsync(TimeSpan.FromSeconds(5), token);
        Check(f.Stub.Resolutions == f.Contract.Methods.Count, "facts Bind resolves each method exactly once");
        f.Stub.ForbidResolution = true;
        var held = rpc.Service.HoldAsync(token).AsTask(); await f.Service.Entered.Task.WaitAsync(token);
        var queued = rpc.Service.PlainAsync().AsTask(); await f.Queued.Task.WaitAsync(token);
        Check(!queued.IsCompleted && f.Admissions.Any(m => m == (f.Contract.ContractId, f.Method(nameof(IFactsRpc.PlainAsync)).MethodId, RpcMethodKind.Unary)),
            "facts queued admission uses projection of captured descriptor");
        f.Service.Release.TrySetResult(); await held.WaitAsync(token);
        Check(await queued.WaitAsync(token) == 42, "facts queued continuation invokes without resolver reacquisition");
        await rpc.Service.NoResultAsync();
        await rpc.Service.FireAsync(); await f.Service.Fired.Task.WaitAsync(token);
        Check(await rpc.Service.NullableAsync(token) is null, "facts real nullable Unary and OneWay complete on bound transport");
        await ExpectCode(() => rpc.Service.FailAsync(token).AsTask(), SharpLinkErrorCode.FailedPrecondition,
            "facts no payload asynchronous exception maps without requery");
        await ExpectCode(() => rpc.Service.FailResultAsync(token).AsTask(), SharpLinkErrorCode.FailedPrecondition,
            "facts payload asynchronous exception maps without requery");
        Check(f.Mapper.Seen.Count == 2 && f.Mapper.Seen.All(x => ReferenceEquals(x.Error, f.Service.Failure) &&
            (x.Method == f.Method(nameof(IFactsRpc.FailAsync)) || x.Method == f.Method(nameof(IFactsRpc.FailResultAsync)))),
            "facts mapper preserves original exception and complete method without interceptor context");
        var interceptor = new FactsInterceptor(); f.Server.ReplaceInterceptors([interceptor]);
        Check(await rpc.Service.PlainAsync() == 42, "facts interceptor terminal forwards captured descriptor to generated Stub");
        await ExpectCode(() => rpc.Service.FailResultAsync(token).AsTask(), SharpLinkErrorCode.FailedPrecondition,
            "facts intercepted exception mapping uses captured method");
        Check(interceptor.Seen.All(m => m == f.Method(nameof(IFactsRpc.PlainAsync)) || m == f.Method(nameof(IFactsRpc.FailResultAsync))) &&
            f.Mapper.Seen.All(x => ReferenceEquals(x.Error, f.Service.Failure)), "facts interceptor generation and mapper retain original identities");
        Check(serverActivities.Count != 0 && serverActivities.All(x => x.Contract == f.Contract.ContractId &&
            f.Contract.Methods.Any(m => m.MethodId == x.Method)), "facts telemetry observes captured known identities without fallback");
        Check(f.Stub.Resolutions == f.Contract.Methods.Count && f.Stub.Invocations.All(m => f.Contract.Methods.Any(known => known.Facts == m)),
            "facts all business admission observer and error paths use bound facts without repeat resolution");
        await FactsGuardControlsAsync(f, site, token);
    }

    private static async Task DirectGeneratedFactsAsync(FactsFixture f, CancellationToken token)
    {
        var bridge = new FactsBridge(); var output = new ArrayBufferWriter<byte>();
        var method = f.Method(nameof(IFactsRpc.PlainAsync));
        await f.Stub.Inner.InvokeAsync(f.Service, bridge, method, 1, ReadOnlySequence<byte>.Empty, output);
        Check(output.WrittenCount == sizeof(int), "facts external readonly response invocation signature is usable");
        await f.Stub.Inner.InvokeNoReturnAsync(f.Service, bridge, f.Method(nameof(IFactsRpc.NoResultAsync)), 2, ReadOnlySequence<byte>.Empty);
        Check(f.Service.Calls == 2, "facts external readonly no return invocation signature is usable");
        output.Clear();
        await f.Stub.Inner.InvokeCancellableAsync(f.Service, bridge, f.Method(nameof(IFactsRpc.ConsumeAsync)), 3, ReadOnlySequence<byte>.Empty, output, token);
        Check(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(output.WrittenSpan) == 3,
            "facts generated client streaming Stub consumes original complete method");
        await f.Stub.Inner.InvokeNoReturnCancellableAsync(f.Service, bridge, f.Method(nameof(IFactsRpc.ProduceAsync)), 4, ReadOnlySequence<byte>.Empty, token);
        Check(bridge.Output.SequenceEqual(new[] { 3, 4 }) && bridge.Methods.Single() == f.Method(nameof(IFactsRpc.ProduceAsync)),
            "facts generated server streaming bridge receives full original descriptor");
        bridge.Output.Clear();
        await f.Stub.Inner.InvokeNoReturnCancellableAsync(f.Service, bridge, f.Method(nameof(IFactsRpc.DuplexAsync)), 5, ReadOnlySequence<byte>.Empty, token);
        Check(bridge.Output.SequenceEqual(new[] { 2, 4 }) && bridge.Methods.Last() == f.Method(nameof(IFactsRpc.DuplexAsync)),
            "facts generated duplex bridge preserves original descriptor");
        await f.Stub.Inner.InvokeNoReturnCancellableAsync(f.Service, bridge, f.Method(nameof(IFactsRpc.FireStreamAsync)), 6, ReadOnlySequence<byte>.Empty, token);
        Check(f.Method(nameof(IFactsRpc.FireStreamAsync)).ClientStreamCount == 1 &&
            f.Method(nameof(IFactsRpc.FireStreamAsync)).SupportsCancellation, "facts OneWay client stream shape is retained without transport enablement");
        var calls = f.Service.Calls;
        await ExpectCode(() => f.Stub.Inner.InvokeAsync(f.Service, bridge, method with { ContractId = long.MaxValue }, 7,
            ReadOnlySequence<byte>.Empty, output).AsTask(), SharpLinkErrorCode.FailedPrecondition,
            "facts external Stub rejects foreign resolved contract before business work");
        Check(f.Service.Calls == calls, "facts foreign Stub identity executes zero business work");
    }

    private static async Task FactsGuardControlsAsync(FactsFixture f, StaticBindingCallSite site, CancellationToken token)
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var method = f.Method(nameof(IFactsRpc.PlainAsync)); var intCodec = f.Client.RuntimeContext.Codecs.GetCodec<int>();
        await Expect<ArgumentNullException>(() => site.InvokeUnaryAsync(method with { ContractId = long.MaxValue }, default(RpcEmptyRequest),
            null!, intCodec, null, canceled.Token).AsTask(), "facts Unary Codec null remains ahead of identity and cancellation");
        await ExpectCode(() => site.InvokeUnaryAsync(method with { ContractId = long.MaxValue }, default(RpcEmptyRequest),
            RpcEmptyRequestCodec.Instance, intCodec, null, canceled.Token).AsTask(), SharpLinkErrorCode.FailedPrecondition,
            "facts Unary scoped identity precedes caller cancellation");
        await Expect<OperationCanceledException>(() => site.InvokeUnaryAsync(method, default(RpcEmptyRequest), RpcEmptyRequestCodec.Instance,
            intCodec, null, canceled.Token).AsTask(), "facts valid Unary preserves caller cancellation boundary");
        var oneWay = f.Method(nameof(IFactsRpc.FireAsync)); var writer = new ForbiddenStreamWriter(); var starts = ForbiddenStreamWriter.Starts;
        await Expect<ArgumentNullException>(() => site.InvokeOneWayAsync(oneWay with { ContractId = long.MaxValue }, default(RpcEmptyRequest),
            null!, writer, null, canceled.Token).AsTask(), "facts OneWay Codec null remains ahead of identity and cancellation");
        var pending = site.InvokeClientStreamingAsync(f.Method(nameof(IFactsRpc.ConsumeAsync)), default(RpcEmptyRequest),
            null!, intCodec, writer, null, canceled.Token);
        Check(pending.IsCompleted && !pending.IsCompletedSuccessfully, "facts unsupported ClientStreaming remains asynchronous fault at invocation");
        await ExpectCode(() => pending.AsTask(), SharpLinkErrorCode.FailedPrecondition,
            "facts unsupported ClientStreaming preserves fail closed priority");
        await ExpectCode(() => { site.InvokeServerStreamingAsync(f.Method(nameof(IFactsRpc.ProduceAsync)), default(RpcEmptyRequest), null!, intCodec, null, canceled.Token); return Task.CompletedTask; },
            SharpLinkErrorCode.FailedPrecondition, "facts unsupported ServerStreaming rejects before enumeration");
        await ExpectCode(() => { site.InvokeDuplexStreamingAsync(f.Method(nameof(IFactsRpc.DuplexAsync)), default(RpcEmptyRequest), null!, intCodec, writer, null, canceled.Token); return Task.CompletedTask; },
            SharpLinkErrorCode.FailedPrecondition, "facts unsupported Duplex rejects before enumeration");
        Check(ForbiddenStreamWriter.Starts == starts, "facts unsupported streaming starts no producer");
    }

    private static void WrongFactsAbiPreflight()
    {
        var context = new AssemblyLoadContext("775-previous-ABI", isCollectible: true);
        try
        {
            var assembly = context.LoadFromAssemblyPath(Path.GetFullPath("eng/775-slice/PreviousAbi/bin/Release/net10.0/SharpLink.PreviousAbi.dll"));
            var result = SharpLinkAssemblyManifestLoader.TryLoad(assembly, out var manifest);
            Check(!result.Succeeded && result.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.IncompatibleManifest && manifest is null,
                "facts previous ABI locator fails metadata preflight");
            Check((int)assembly.GetType("PreviousAbi.Manifest")!.GetField("Constructions")!.GetValue(null)! == 0,
                "facts previous ABI rejection constructs no manifest or Stub");
        }
        finally { context.Unload(); }
    }
}
