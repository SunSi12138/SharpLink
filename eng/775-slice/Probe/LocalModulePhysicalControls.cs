using System.Runtime.CompilerServices;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;

namespace SharpLink.BindingSlice;

internal static partial class Program
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference, IRpcContractChannel?)> LocalConsumerAsync(LocalModuleFixture f, CancellationToken token)
    {
        var signals = new GroupSignals(); var nextSignals = new GroupSignals();
        var old = new LoadedGroup(GroupPath(), 1, signals); var next = new LoadedGroup(GroupPath(), 2, nextSignals);
        if (!RegisterLocal(f, old)) throw new InvalidOperationException("Local registration failed.");
        var driver = new LocalDriver(old); driver.Create(f.Client); var channel = f.Client.CreateContractChannel(driver.Local(f.Client)); var module = f.Module(old);
        await f.ConnectAsync(token); await driver.Prepare(token);
        var policy = new LocalPolicy(); f.Client.ReplaceInterceptors([policy]);
        var entered = Signal(); using var release = new ManualResetEventSlim();
        old.Set("Decode", (Action)(() => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Local consumer Codec cut not released."); }));
        using var caller = new CancellationTokenSource();
        try
        {
            var reading = ReadAllAsync(driver.Produce(caller.Token), caller.Token); await entered.Task.WaitAsync(token);
            var originalCancellation = policy.Cancellation!;
            Check(module.RemainingCalls == 1 && module.RemainingStreams == 1 && signals.Count("decode") == 1,
                "local consumer active original Codec owns exact local stream lease");
            var replacing = ReplaceLocal(f, old, next).AsTask(); caller.Cancel();
            Check(!replacing.IsCompleted && module.State == SharpLinkDynamicModuleState.Draining && module.RemainingStreams == 1,
                "local consumer retirement and cancellation cannot reclaim in-flight original Codec");
            Check(!originalCancellation.IsDisposed && originalCancellation.Token.IsCancellationRequested,
                "local consumer original linked CTS remains usable during physical decoder exit");
            release.Set(); await Expect<OperationCanceledException>(() => reading, "local consumer selected cancellation survives original Codec physical completion");
            var result = await replacing.WaitAsync(token);
            Check(result.ReferencesReleased && module.RemainingCalls == 0 && module.RemainingStreams == 0 && originalCancellation.IsDisposed,
                "local consumer original physical disposal permits local module release");
            Check(signals.Count("decode") == 1 && nextSignals.Count("decode") == 0,
                "local consumer old response cannot be decoded using replacement Codec");
            var successor = new LocalDriver(next); successor.Create(f.Client); await successor.Prepare(token);
            Check((await ReadAllAsync(successor.Produce(token), token)).SequenceEqual(new[] { 20 }) && nextSignals.Count("decode") == 1,
                "local consumer fresh proxy uses only the replacement local Codec generation");
            await UnregisterLocal(f, next); successor.Drop();
        }
        finally { release.Set(); }
        driver.Drop(); return (old.Weak, channel);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference, IRpcContractChannel?)> LocalDuplexAsync(LocalModuleFixture f, CancellationToken token)
    {
        var signals = new GroupSignals(); var nextSignals = new GroupSignals();
        var old = new LoadedGroup(GroupPath(), 1, signals); var next = new LoadedGroup(GroupPath(), 2, nextSignals);
        if (!RegisterLocal(f, old)) throw new InvalidOperationException("Local registration failed.");
        var driver = new LocalDriver(old); driver.Create(f.Client); var channel = f.Client.CreateContractChannel(driver.Local(f.Client)); var module = f.Module(old);
        await f.ConnectAsync(token); await driver.Prepare(token);
        var policy = new LocalPolicy(); f.Client.ReplaceInterceptors([policy]);
        var entered = Signal(); using var releaseDecode = new ManualResetEventSlim(); var cleanup = Signal();
        old.Set("Decode", (Action)(() => { entered.TrySetResult(); if (!releaseDecode.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Local duplex Codec cut not released."); }));
        old.Set("ProducerCleanup", (Func<Task>)(() => cleanup.Task));
        var producer = new ProbeProducer { HoldAfterFirst = true, HoldDispose = true }; using var caller = new CancellationTokenSource();
        try
        {
            var reading = ReadAllAsync(driver.Duplex(producer, caller.Token), caller.Token); await entered.Task.WaitAsync(token);
            var originalCancellation = policy.Cancellation!;
            Check(producer.Starts == 1 && module.RemainingCalls == 2 && module.RemainingStreams == 2 && signals.Count("encode") == 1,
                "local duplex producer and consumer hold independent original physical owners");
            var replacing = ReplaceLocal(f, old, next).AsTask(); caller.Cancel();
            producer.ReleaseMove.TrySetResult(); await producer.DisposeEntered.Task.WaitAsync(token);
            Check(!replacing.IsCompleted && module.RemainingStreams == 2 && !originalCancellation.IsDisposed,
                "local duplex cancellation retains both held producer Dispose and consumer Codec");
            producer.ReleaseDispose.TrySetResult(); await UntilAsync(() => signals.Count("producer-cleanup") == 1, token);
            releaseDecode.Set(); await Expect<OperationCanceledException>(() => reading, "local duplex consumer exits with original cancellation after decoder drain");
            await UntilAsync(() => module.RemainingCalls == 1, token);
            Check(module.RemainingStreams == 1 && !replacing.IsCompleted && !originalCancellation.IsDisposed && originalCancellation.Token.IsCancellationRequested,
                "local duplex consumer exit cannot release independent producer lease or linked CTS");
            cleanup.TrySetResult(); var result = await replacing.WaitAsync(token);
            Check(result.ReferencesReleased && module.RemainingCalls == 0 && module.RemainingStreams == 0 && originalCancellation.IsDisposed,
                "local duplex final producer exit completes retirement of both original owners");
            Check(signals.Count("encode") == 1 && signals.Count("decode") == 1 && nextSignals.Count("encode") == 0 && nextSignals.Count("decode") == 0,
                "local duplex original facts and both Codec directions never retarget mid-call");
            var successor = new LocalDriver(next); successor.Create(f.Client); await successor.Prepare(token);
            Check((await ReadAllAsync(successor.Duplex(new ProbeProducer(), token), token)).SequenceEqual(new[] { 22, 24 }),
                "local duplex fresh local generation completes on the unchanged bound server");
            await UnregisterLocal(f, next); successor.Drop();
        }
        finally { producer.Release(); cleanup.TrySetResult(); releaseDecode.Set(); }
        driver.Drop(); return (old.Weak, channel);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference, IRpcContractChannel?)> LocalQueuedHeadAsync(LocalModuleFixture f, bool duplex, CancellationToken token)
    {
        var prefix = duplex ? "local queued duplex" : "local queued server";
        var old = new LoadedGroup(GroupPath(), 1, new GroupSignals()); var next = new LoadedGroup(GroupPath(), 2, new GroupSignals());
        if (!RegisterLocal(f, old)) throw new InvalidOperationException("Local registration failed.");
        var driver = new LocalDriver(old); driver.Create(f.Client); var channel = f.Client.CreateContractChannel(driver.Local(f.Client)); var module = f.Module(old);
        await f.ConnectAsync(token); await driver.Prepare(token);
        var entered = Signal(); var release = Signal(); var policy = new LocalPolicy(); f.Client.ReplaceInterceptors([policy]);
        f.Client.BeforeBoundStreamRegistrationTestHook = async _ => { entered.TrySetResult(); await release.Task; };
        var producer = new ProbeProducer(); using var caller = new CancellationTokenSource();
        try
        {
            var reading = ReadAllAsync(duplex ? driver.Duplex(producer, caller.Token) : driver.Produce(caller.Token), caller.Token);
            await entered.Task.WaitAsync(token);
            Check(module.RemainingCalls == (duplex ? 2 : 1) && producer.Starts == 0, prefix + " transfers original physical ownership before background registration");
            caller.Cancel(); await Expect<OperationCanceledException>(() => reading, prefix + " consumer may finish logically while head is queued");
            var replacing = ReplaceLocal(f, old, next).AsTask();
            Check(!replacing.IsCompleted && module.RemainingStreams == (duplex ? 2 : 1) && !policy.Cancellation!.IsDisposed,
                prefix + " queued head and producer retain protection after consumer exit");
            release.TrySetResult(); var result = await replacing.WaitAsync(token);
            Check(result.ReferencesReleased && module.RemainingCalls == 0 && module.RemainingStreams == 0 && producer.Starts == 0,
                prefix + " canceled registration drops original owners without producer entry");
        }
        finally { release.TrySetResult(); f.Client.BeforeBoundStreamRegistrationTestHook = null; }
        await UnregisterLocal(f, next); next.Drop(); driver.Drop(); return (old.Weak, channel);
    }

    private static async Task LocalTimedOutStopAsync(string transport, CancellationToken token)
    {
        await using var f = new LocalModuleFixture(transport);
        var old = new LoadedGroup(GroupPath(), 1, new GroupSignals());
        if (!RegisterLocal(f, old)) throw new InvalidOperationException("Local registration failed.");
        var driver = new LocalDriver(old); driver.Create(f.Client); var module = f.Module(old);
        await f.ConnectAsync(token); await driver.Prepare(token);
        var policy = new LocalPolicy(); f.Client.ReplaceInterceptors([policy]);
        var producer = new ProbeProducer { HoldMove = true, HoldDispose = true };
        try
        {
            var call = driver.Consume(producer, token).AsTask(); await producer.Entered.Task.WaitAsync(token);
            var originalCancellation = policy.Cancellation!;
            var result = await UnregisterLocal(f, old, TimeSpan.Zero);
            Check(!result.ReferencesReleased && result.RemainingCalls > 0 && result.RemainingStreams > 0,
                "local timeout reports unreleased physical owners rather than successful reclaim");
            await Expect<OperationCanceledException>(() => call, "local timeout forced module cancellation reaches original pending call");
            Check(module.State == SharpLinkDynamicModuleState.DrainTimedOut && module.RemainingStreams == 1 && !originalCancellation.IsDisposed,
                "local timeout keeps original producer and CTS after logical cancellation");
            var stop = f.Client.StopAsync().AsTask();
            Check(!stop.IsCompleted, "local timeout Client Stop joins outstanding physical local module cleanup");
            producer.ReleaseMove.TrySetResult(); await producer.DisposeEntered.Task.WaitAsync(token);
            Check(!stop.IsCompleted && !originalCancellation.IsDisposed && originalCancellation.Token.IsCancellationRequested,
                "local timeout asynchronous producer disposal retains original linked CTS during Stop");
            producer.ReleaseDispose.TrySetResult(); await stop.WaitAsync(token);
            Check(module.State == SharpLinkDynamicModuleState.Released && module.RemainingCalls == 0 && module.RemainingStreams == 0 && originalCancellation.IsDisposed,
                "local timeout physical completion releases module CTS and Stop drain");
        }
        finally { producer.Release(); }
        driver.Drop();
    }
}
