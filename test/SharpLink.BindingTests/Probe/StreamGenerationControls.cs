using System.Runtime.CompilerServices;
using SharpLink.Abstractions;
using SharpLink.GroupPlugin;
using SharpLink.Runtime;

namespace SharpLink.BindingTests;

internal static partial class Program
{
    private static async Task RetiredStreamGenerationAsync(string transport, bool outbound, CancellationToken token)
    {
        var prefix = outbound ? "stream outbound generation" : "stream inbound generation";
        await using var f = new GroupFixture(transport);
        var signals = new GroupSignals(); var nextSignals = new GroupSignals();
        var old = new LoadedGroup(GroupPath(), 1, signals); var next = new LoadedGroup(GroupPath(), 2, nextSignals);
        var entered = Signal(); var iteratorCleanup = Signal(); var releaseIterator = Signal();
        using var releaseDecode = new ManualResetEventSlim();
        Check(f.Server.RegisterAssembly(old.Assembly).Succeeded, prefix + " registers original module");
        await f.ConnectAsync(token);
        var proxy = f.Client.CreateProxy<IHotRpc>(); await proxy.PrepareAsync(TimeSpan.FromSeconds(5), token);
        var module = f.Module(old);
        old.Set("Cleanup", (Func<Task>)(() => signals.ReleaseCleanup.Task));
        if (outbound)
        {
            old.Set("StreamCleanup", (Func<Task>)(() => { iteratorCleanup.TrySetResult(); return releaseIterator.Task; }));
        }
        else old.Set("Decode", (Action)(() =>
        { entered.TrySetResult(); if (!releaseDecode.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Input Codec cut was not released."); }));
        try
        {
            Task<int>? input = null; IAsyncEnumerator<HotPayload>? output = null;
            if (outbound)
            {
                output = proxy.Service.ProduceAsync(token).GetAsyncEnumerator(token);
                Check(await output.MoveNextAsync() && output.Current.Value == 100, prefix + " uses original outbound Codec and iterator");
                await iteratorCleanup.Task.WaitAsync(token);
            }
            else
            {
                input = proxy.Service.ConsumeAsync(HotValues(), token).AsTask(); await entered.Task.WaitAsync(token);
                Check(!input.IsCompleted && signals.Count("decode") == 1, prefix + " holds original inbound Codec physically");
            }
            Check(module.RemainingCalls == 1 && module.RemainingStreams == 1, prefix + " promotes original call lease to stream without reacquire");
            var replacing = ReplaceGroupAtBoundary(f, old, next, token).AsTask();
            Check(!replacing.IsCompleted && module.State == SharpLinkDynamicModuleState.Draining && module.RemainingStreams == 1,
                prefix + " retirement cannot release active stream generation");
            if (outbound)
            {
                await output!.DisposeAsync();
                Check(!replacing.IsCompleted && !module.BindingPhysicalReleased.Task.IsCompleted,
                    prefix + " consumer abandon cannot pass held iterator Dispose");
                releaseIterator.TrySetResult();
            }
            else releaseDecode.Set();
            await signals.CleanupEntered.Task.WaitAsync(token);
            Check(!replacing.IsCompleted && module.RemainingCalls == 1 && module.RemainingStreams == 1,
                prefix + " asynchronous service cleanup retains original physical ownership");
            signals.ReleaseCleanup.TrySetResult();
            if (!outbound) Check(await input!.WaitAsync(token) == 1017, prefix + " decode and business result never retarget to replacement");
            else Check(signals.Count("encode") == 1 && signals.Count("iterator-exited") == 1 && nextSignals.Count("encode") == 0,
                prefix + " outbound physical tail uses only original Codec and iterator");
            var result = await replacing.WaitAsync(token);
            Check(result.Succeeded && module.RemainingCalls == 0 && module.RemainingStreams == 0 && module.BindingPhysicalReleased.Task.IsCompletedSuccessfully,
                prefix + " physical exit permits retirement and quota reclamation");
            old.Drop();
            GCheck(Collected(old.Weak), prefix, "collectible old ALC releases while original clients and server stay live");
            GC.KeepAlive(proxy); GC.KeepAlive(f.Client); GC.KeepAlive(f.Server);
        }
        finally { releaseDecode.Set(); releaseIterator.TrySetResult(); signals.ReleaseCleanup.TrySetResult(); }
        await UnregisterAndDropGroupAsync(f, next, token);
    }
    private static async IAsyncEnumerable<HotPayload> HotValues()
    { await Task.Yield(); yield return new HotPayload(7); }
}
