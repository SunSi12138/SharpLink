using System.Runtime.CompilerServices;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.GroupPlugin;
using SharpLink.Runtime;
using SharpLink.Server;

namespace SharpLink.BindingSlice;
internal static partial class Program
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference, IRpcContractChannel?)> OneWayLocalOwnerAsync(LocalModuleFixture f, CancellationToken ct)
    {
        var signals = new GroupSignals(); var old = new LoadedGroup(GroupPath(), 1, signals);
        if (!RegisterLocal(f, old)) throw new InvalidOperationException("Local registration failed.");
        var driver = new LocalDriver(old); driver.Create(f.Client); var channel = f.Client.CreateContractChannel(driver.Local(f.Client));
        var module = f.Module(old); await f.ConnectAsync(ct); await driver.Prepare(ct);
        var policy = new LocalPolicy(); f.Client.ReplaceInterceptors([policy]);
        var cleanup = Signal(); old.Set("ProducerCleanup", (Func<Task>)(() => cleanup.Task));
        var producer = new ProbeProducer { HoldAfterFirst = true, HoldDispose = true }; using var caller = new CancellationTokenSource();
        try
        {
            var call = driver.FireInput(producer, caller.Token).AsTask();
            await UntilAsync(() => signals.Count("encode") == 2, ct);
            var original = policy.Cancellation!; var connection = f.Connection;
            Check(module.RemainingCalls == 1 && module.RemainingStreams == 1 && connection.PendingCalls.PhysicalStreamProducers == 1,
                "oneway local producer retains original wrapper lease Codec and pending physical owner without Ack");
            var retired = await UnregisterLocal(f, old, TimeSpan.Zero); caller.Cancel();
            Check(!retired.ReferencesReleased && module.State == SharpLinkDynamicModuleState.DrainTimedOut && !original.IsDisposed && original.Token.IsCancellationRequested,
                "oneway local forced retirement preserves original linked CTS during noncooperative producer");
            await ExpectCode(() => channel.InvokeOneWayAsync(driver.Method(nameof(IHotRpc.FireInputAsync)), default(RpcEmptyRequest), null!, new ForbiddenStreamWriter(), null, caller.Token).AsTask(),
                SharpLinkErrorCode.Unavailable, "oneway local retired wrapper guard still wins over Codec and cancellation");
            producer.ReleaseMove.TrySetResult(); await producer.DisposeEntered.Task.WaitAsync(ct);
            Check(connection.PendingCalls.Count == 0 && connection.PendingCalls.PhysicalStreamProducers == 1 && module.RemainingStreams == 1 && !original.IsDisposed,
                "oneway local logical terminal cannot reclaim held asynchronous producer Dispose");
            producer.ReleaseDispose.TrySetResult(); await UntilAsync(() => signals.Count("producer-cleanup") == 1, ct);
            Check(!call.IsCompleted && module.RemainingStreams == 1 && !original.IsDisposed,
                "oneway local collectible producer cleanup retains original physical ownership");
            cleanup.TrySetResult(); await Expect<OperationCanceledException>(() => call, "oneway local original cancellation survives complete physical producer exit");
            await UntilAsync(() => module.State == SharpLinkDynamicModuleState.Released, ct);
            Check(module.RemainingCalls == 0 && module.RemainingStreams == 0 && original.IsDisposed && connection.PendingCalls.PhysicalStreamProducers == 0,
                "oneway local final producer exit releases original module CTS and physical quota");
            Check(signals.Count("encode") == 2 && signals.Count("producer-exited") == 1,
                "oneway local original header and item Codec never execute after physical retirement");
            var flow = OneWayFixture.Flow(connection.Session);
            await UntilAsync(() => flow.RetainedSendStreamCount == 0, ct);
            Check(flow.SendConnectionCredit == connection.Session.RuntimeContext.FlowControl.ConnectionReceiveWindowBytes && connection.Session.IsConnected,
                "oneway local physical retirement returns sub-threshold credit without closing Session or late stream terminal");
            Check(OneWayPoolHasNoExceptions(flow), "oneway local pooled send states retain no cancellation exception or collectible stack");
        }
        finally { producer.Release(); cleanup.TrySetResult(); }
        driver.Drop(); return (old.Weak, channel);
    }

    private static async Task OneWayServerOwnerAsync(string transport, CancellationToken ct)
    {
        await using var f = new GroupFixture(transport); var signals = new GroupSignals(); var nextSignals = new GroupSignals();
        var old = new LoadedGroup(GroupPath(), 1, signals); var next = new LoadedGroup(GroupPath(), 2, nextSignals);
        Check(f.Server.RegisterAssembly(old.Assembly).Succeeded, "oneway server registers original typed consumer module");
        await f.ConnectAsync(ct); var proxy = f.Client.CreateProxy<IHotRpc>(); await proxy.PrepareAsync(TimeSpan.FromSeconds(5), ct);
        var control = f.Client.CreateProxy<ISliceRpc>(); await control.PrepareAsync(TimeSpan.FromSeconds(5), ct);
        var module = f.Module(old); var entered = Signal(); var gateRelease = Signal(); var decodeEntered = Signal();
        using var releaseDecode = new ManualResetEventSlim();
        f.Server.ReplaceInterceptors([new OneWayPassGate(entered, gateRelease)]);
        old.Set("Decode", (Action)(() => { decodeEntered.TrySetResult(); if (!releaseDecode.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("OneWay original Codec cut not released."); }));
        old.Set("Cleanup", (Func<Task>)(() => signals.ReleaseCleanup.Task));
        try
        {
            await proxy.Service.FireInputAsync(new(7), HotValues(), ct); await entered.Task.WaitAsync(ct);
            Check(await control.Service.PingAsync() == 42, "oneway server nonempty head survives reader progress during asynchronous interceptor");
            gateRelease.TrySetResult(); await decodeEntered.Task.WaitAsync(ct);
            Check(module.RemainingCalls == 1 && module.RemainingStreams == 1 && signals.Count("decode") == 1,
                "oneway server original registration facts and Codec remain physically owned after local send completion");
            var replacing = ReplaceGroupAtBoundary(f, old, next, ct).AsTask();
            Check(!replacing.IsCompleted && module.State == SharpLinkDynamicModuleState.Draining,
                "oneway server retirement cannot pass original input Codec dispatch");
            releaseDecode.Set(); await signals.CleanupEntered.Task.WaitAsync(ct);
            Check(signals.Count("decode") == 2 && signals.Count("oneway-sum-34") == 1 && nextSignals.Count("decode") == 0,
                "oneway server owned nonempty head and typed input use only original Codec generation");
            Check(!replacing.IsCompleted && module.RemainingStreams == 1,
                "oneway server asynchronous service disposal retains original physical stream protection");
            signals.ReleaseCleanup.TrySetResult(); var result = await replacing.WaitAsync(ct);
            Check(result.Succeeded && module.RemainingCalls == 0 && module.RemainingStreams == 0 && module.BindingPhysicalReleased.Task.IsCompleted,
                "oneway server physical consumer exit permits group retirement and quota reclaim");
            old.Drop(); GCheck(Collected(old.Weak), "oneway server ALC", "old ALC collects with original client server and proxy live");
            GC.KeepAlive(proxy); GC.KeepAlive(f.Server); GC.KeepAlive(f.Client);
        }
        finally { gateRelease.TrySetResult(); releaseDecode.Set(); signals.ReleaseCleanup.TrySetResult(); f.Server.ReplaceInterceptors([]); }
        await UnregisterAndDropGroupAsync(f, next, ct);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool OneWayPoolHasNoExceptions(StreamFlowController flow)
    {
        var state = Field<object?>(flow, "_pooledSendStates");
        while (state is not null)
        {
            if (state.GetType().GetField("AbortException")!.GetValue(state) is not null) return false;
            state = state.GetType().GetField("Next")!.GetValue(state);
        }
        return true;
    }
    private sealed class OneWayPassGate(TaskCompletionSource entered, TaskCompletionSource release) : ISharpLinkServerInterceptor
    {
        public async ValueTask InvokeAsync(SharpLinkServerInvocationContext context, SharpLinkServerInvocationDelegate next)
        { if (context.Method.Kind == RpcMethodKind.OneWay) { entered.TrySetResult(); await release.Task; } await next(context); }
    }
}

