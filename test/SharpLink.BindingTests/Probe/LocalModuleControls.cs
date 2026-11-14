using System.Runtime.CompilerServices;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.GroupPlugin;
using SharpLink.Runtime;

namespace SharpLink.BindingTests;

internal static partial class Program
{
    private static async Task RunLocalModulesAsync(string transport, CancellationToken token)
    {
        await LocalAlcAsync(transport, "local guards", LocalGuardsAsync, token);
        await LocalAlcAsync(transport, "local policy", LocalPolicyAsync, token);
        await LocalAlcAsync(transport, "local producer", LocalProducerAsync, token);
        await LocalAlcAsync(transport, "local consumer", LocalConsumerAsync, token);
        await LocalAlcAsync(transport, "local duplex", LocalDuplexAsync, token);
        await LocalAlcAsync(transport, "local queued server", (f, ct) => LocalQueuedHeadAsync(f, false, ct), token);
        await LocalAlcAsync(transport, "local queued duplex", (f, ct) => LocalQueuedHeadAsync(f, true, ct), token);
        await LocalTimedOutStopAsync(transport, token);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference, IRpcContractChannel?)> LocalGuardsAsync(LocalModuleFixture f, CancellationToken token)
    {
        var old = new LoadedGroup(GroupPath(), 1, new GroupSignals()); var driver = new LocalDriver(old);
        Check(RegisterLocal(f, old), "local guards dynamic registration succeeds before Connect");
        driver.Create(f.Client); var local = driver.Local(f.Client); var channel = f.Client.CreateContractChannel(local);
        var module = f.Module(old); var unary = driver.Method(nameof(IHotRpc.GetAsync));
        var consume = driver.Method(nameof(IHotRpc.ConsumeAsync)); var produce = driver.Method(nameof(IHotRpc.ProduceAsync)); var duplex = driver.Method(nameof(IHotRpc.DuplexAsync));
        var codec = f.Client.RuntimeContext.Codecs.GetCodec<int>(); var writer = new ForbiddenStreamWriter(); var starts = ForbiddenStreamWriter.Starts;
        Check(module.RemainingCalls == 0 && f.Client.ReadyConnectionCount == 0 && f.Client.BindingBudget.Operations == 0,
            "local guards factories are local and hold no lifetime lease");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Expect<ArgumentNullException>(() => channel.InvokeUnaryAsync(unary, default(RpcEmptyRequest), null!, codec, null, canceled.Token).AsTask(),
            "local guards running wrapper acquire still precedes inner Codec null");
        await Expect<OperationCanceledException>(() => channel.InvokeClientStreamingAsync(consume, default(RpcEmptyRequest), RpcEmptyRequestCodec.Instance, codec, writer, null, canceled.Token).AsTask(),
            "local guards valid ClientStreaming cancellation releases both provisional owners");
        Check(module.RemainingCalls == 0 && module.RemainingStreams == 0, "local guards failed entry returns both call and producer leases");
        var deferred = channel.InvokeServerStreamingAsync(produce, default(RpcEmptyRequest), null!, codec, null, canceled.Token);
        var deferredDuplex = channel.InvokeDuplexStreamingAsync(duplex, default(RpcEmptyRequest), null!, codec, writer, null, canceled.Token);
        var expired = channel.InvokeServerStreamingAsync(produce with { HasMethodTimeout = true, MethodTimeout = TimeSpan.FromSeconds(5) },
            default(RpcEmptyRequest), null!, codec, null, token);
        var expiredDuplex = channel.InvokeDuplexStreamingAsync(duplex with { HasMethodTimeout = true, MethodTimeout = TimeSpan.FromSeconds(5) },
            default(RpcEmptyRequest), null!, codec, writer, null, token);
        Check(module.RemainingCalls == 0, "local guards stream invocation captures without acquire or Codec validation");
        var retired = await UnregisterLocal(f, old);
        Check(retired.ReferencesReleased && module.State == SharpLinkDynamicModuleState.Released, "local guards idle unregister physically releases local generation");
        var call = channel.InvokeUnaryAsync(unary, default(RpcEmptyRequest), null!, codec, null, canceled.Token);
        Check(call.IsCompleted && !call.IsCompletedSuccessfully, "local guards retired Unary is a returned fault rather than synchronous Codec throw");
        await ExpectCode(() => call.AsTask(), SharpLinkErrorCode.Unavailable, "local guards retired Unary wins over Codec and caller cancellation");
        await ExpectCode(() => channel.InvokeOneWayAsync(unary with { Kind = RpcMethodKind.OneWay }, default(RpcEmptyRequest), null!, writer, null, canceled.Token).AsTask(),
            SharpLinkErrorCode.Unavailable, "local guards retired OneWay wins over Codec and caller cancellation");
        await ExpectCode(() => channel.InvokeClientStreamingAsync(consume, default(RpcEmptyRequest), null!, codec, writer, null, canceled.Token).AsTask(),
            SharpLinkErrorCode.Unavailable, "local guards retired ClientStreaming wins before both inner guards");
        await ExpectCode(() => ReadAllAsync(deferred, token), SharpLinkErrorCode.Unavailable, "local guards ServerStreaming retirement is checked at first enumeration before Codec");
        await ExpectCode(() => ReadAllAsync(deferredDuplex, token), SharpLinkErrorCode.Unavailable, "local guards Duplex retirement is checked at first enumeration before Codec");
        f.Clock.Advance(TimeSpan.FromSeconds(5));
        await ExpectCode(() => ReadAllAsync(expired, token), SharpLinkErrorCode.DeadlineExceeded, "local guards original ServerStreaming deadline wins before retired acquire");
        await ExpectCode(() => ReadAllAsync(expiredDuplex, token), SharpLinkErrorCode.DeadlineExceeded, "local guards original Duplex deadline wins before retired acquire");
        await Expect<ArgumentOutOfRangeException>(() => channel.PrepareAsync(TimeSpan.Zero, canceled.Token).AsTask(), "local guards Prepare validates finite budget before cancellation and retirement");
        await Expect<OperationCanceledException>(() => channel.PrepareAsync(TimeSpan.FromSeconds(5), canceled.Token).AsTask(), "local guards Prepare cancellation retains priority before local retirement");
        await ExpectCode(() => channel.PrepareAsync(TimeSpan.FromSeconds(5), token).AsTask(), SharpLinkErrorCode.Unavailable, "local guards retired Prepare cannot report ready");
        await ExpectCode(() => { f.Client.CreateContractChannel(local); return Task.CompletedTask; }, SharpLinkErrorCode.FailedPrecondition,
            "local guards released scalar selector cannot silently resolve a new generation");
        Check(ForbiddenStreamWriter.Starts == starts && module.RemainingCalls == 0, "local guards all rejected families start zero producer and retain zero leases");
        driver.Drop(); return (old.Weak, channel);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference, IRpcContractChannel?)> LocalPolicyAsync(LocalModuleFixture f, CancellationToken token)
    {
        var old = new LoadedGroup(GroupPath(), 1, new GroupSignals()); var driver = new LocalDriver(old);
        if (!RegisterLocal(f, old)) throw new InvalidOperationException("Local registration failed.");
        driver.Create(f.Client); var local = driver.Local(f.Client); var channel = f.Client.CreateContractChannel(local); var module = f.Module(old);
        await f.ConnectAsync(token); await driver.Prepare(token);
        var first = new LocalPolicy(); var next = new LocalPolicy(); f.Client.ReplaceInterceptors([first]);
        var server = driver.Produce(token); var duplex = driver.Duplex(new ProbeProducer(), token);
        Check(module.RemainingCalls == 0 && first.Calls == 0, "local policy stream construction does not acquire or execute interceptor");
        f.Client.ReplaceInterceptors([next]);
        Check((await ReadAllAsync(server, token)).SequenceEqual(new[] { 10 }) && first.Calls == 1 && next.Calls == 0,
            "local policy ServerStreaming uses invocation-time interceptor generation");
        Check((await ReadAllAsync(duplex, token)).SequenceEqual(new[] { 12, 14 }) && first.Calls == 2 && next.Calls == 0,
            "local policy Duplex uses invocation-time interceptor generation");
        var unrelated = new LoadedGroup(Path.GetFullPath("test/SharpLink.BindingTests/GroupPluginUnrelated/bin/Release/net10.0/SharpLink.GroupPluginUnrelated.dll"), 4, new GroupSignals());
        Check(RegisterLocal(f, unrelated), "local policy independent module publication succeeds");
        var retained = driver.Local(f.Client);
        Check(retained.Generation == local.Generation && retained.Hash == local.Hash, "local policy unrelated publication preserves exact local registration generation");
        await f.Client.CreateContractChannel(local).PrepareAsync(TimeSpan.FromSeconds(5), token);
        Check(await driver.Unary() == 0 && await driver.Echo() == 7, "local policy retained selector and generated Unary keep original local Codec owner");
        var waiting = channel.PrepareAsync(TimeSpan.FromSeconds(5), token); await waiting;
        Check(module.RemainingCalls == 0 && module.RemainingStreams == 0, "local policy completed five-family controls release local accounting");
        var ackEntered = Signal(); var releaseAck = Signal(); var producer = new ProbeProducer();
        f.Server.BeforeStreamAcceptanceTestHook = async (_, _, _) => { ackEntered.TrySetResult(); await releaseAck.Task; };
        try
        {
            var head = driver.ConsumeWithHeader(producer, token).AsTask(); await ackEntered.Task.WaitAsync(token);
            Check(producer.Starts == 0 && !head.IsCompleted, "local head nonempty original arguments wait behind per-call acceptance");
            var control = f.Client.CreateProxy<ISliceRpc>(); await control.PrepareAsync(TimeSpan.FromSeconds(5), token);
            Check(await control.Service.PingAsync() == 42 && !head.IsCompleted, "local head later real RPC advances transport while original argument decode is held");
            releaseAck.TrySetResult();
            Check(await head.WaitAsync(token) == 10 && producer.Starts == 1, "local head owned arguments and original Codec survive transport advancement");
            Check(f.Mapper.Failures.IsEmpty, "local head no internal decoder failure is masked by client cancellation");
        }
        finally { releaseAck.TrySetResult(); f.Server.BeforeStreamAcceptanceTestHook = null; }
        await UnregisterLocal(f, unrelated); unrelated.Drop();
        await UnregisterLocal(f, old); driver.Drop(); return (old.Weak, channel);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference, IRpcContractChannel?)> LocalProducerAsync(LocalModuleFixture f, CancellationToken token)
    {
        var signals = new GroupSignals(); var old = new LoadedGroup(GroupPath(), 1, signals); var next = new LoadedGroup(GroupPath(), 2, new GroupSignals());
        if (!RegisterLocal(f, old)) throw new InvalidOperationException("Local registration failed.");
        var driver = new LocalDriver(old); driver.Create(f.Client); var local = driver.Local(f.Client); var channel = f.Client.CreateContractChannel(local); var module = f.Module(old);
        await f.ConnectAsync(token); await driver.Prepare(token);
        var policy = new LocalPolicy(); f.Client.ReplaceInterceptors([policy]);
        var ack = Signal(); var releaseAck = Signal(); var cleanup = Signal(); var producer = new ProbeProducer { HoldMove = true, HoldDispose = true };
        old.Set("ProducerCleanup", (Func<Task>)(() => cleanup.Task));
        f.Server.BeforeStreamAcceptanceTestHook = async (_, _, _) => { ack.TrySetResult(); await releaseAck.Task; };
        using var caller = new CancellationTokenSource();
        try
        {
            var call = driver.Consume(producer, caller.Token).AsTask(); await ack.Task.WaitAsync(token);
            Check(producer.Starts == 0 && module.RemainingCalls == 2 && module.RemainingStreams == 2, "local producer both original owners exist before per-call Ack without producer entry");
            var replacing = ReplaceLocal(f, old, next).AsTask();
            Check(module.State == SharpLinkDynamicModuleState.Draining && !replacing.IsCompleted, "local producer replacement closes old gate without releasing admitted owners");
            await ExpectCode(() => driver.Unary().AsTask(), SharpLinkErrorCode.Unavailable, "local producer retired generated proxy rejects new calls");
            await ExpectCode(() => channel.PrepareAsync(TimeSpan.FromSeconds(5), token).AsTask(), SharpLinkErrorCode.Unavailable, "local producer old ready channel cannot prepare after local retirement");
            releaseAck.TrySetResult();
            await Task.WhenAny(producer.Entered.Task, call).WaitAsync(token);
            if (call.IsCompleted) { _ = await call; throw new InvalidOperationException("Client stream completed before producer entry."); }
            var originalCancellation = policy.Cancellation!; caller.Cancel();
            await Expect<OperationCanceledException>(() => call, "local producer logical cancellation returns while physical producer remains held");
            Check(module.RemainingCalls == 1 && module.RemainingStreams == 1 && !originalCancellation.IsDisposed && originalCancellation.Token.IsCancellationRequested,
                "local producer independent lease and original linked CTS survive logical call exit");
            producer.ReleaseMove.TrySetResult(); await producer.DisposeEntered.Task.WaitAsync(token);
            Check(!replacing.IsCompleted && module.RemainingStreams == 1, "local producer source asynchronous Dispose blocks physical retirement");
            producer.ReleaseDispose.TrySetResult(); await UntilAsync(() => signals.Count("producer-cleanup") == 1, token);
            Check(!replacing.IsCompleted && !originalCancellation.IsDisposed && signals.Count("producer-exited") == 0,
                "local producer collectible writer cleanup retains original CTS and module");
            cleanup.TrySetResult(); var result = await replacing.WaitAsync(token);
            Check(result.Succeeded && result.ReferencesReleased && module.RemainingCalls == 0 && module.RemainingStreams == 0 && originalCancellation.IsDisposed,
                "local producer last physical exit releases original CTS registration and counters");
            await ExpectCode(() => { f.Client.CreateContractChannel(local); return Task.CompletedTask; }, SharpLinkErrorCode.FailedPrecondition,
                "local producer replacement cannot retarget old scalar selector");
        }
        finally { releaseAck.TrySetResult(); producer.Release(); cleanup.TrySetResult(); f.Server.BeforeStreamAcceptanceTestHook = null; }
        await UnregisterLocal(f, next); next.Drop(); driver.Drop(); return (old.Weak, channel);
    }
}
