using System;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Client;
using SharpLink.Runtime;

namespace SharpLink.N1.StaticBindEvidence;
internal static partial class Program
{
    private static async Task ContentionComponentsAsync()
    {
        var gate = new Lock();
        var failed = NonBlockingDiagnostic<object>.Capture(gate, static () => throw new InvalidOperationException("capture failed"));
        Check(failed.State == "failed" && failed.Value is null && failed.FailureType == typeof(InvalidOperationException).FullName,
            "diagnostic-callback-failure-is-explicit-not-zero");
        Check(NonBlockingDiagnostic<object>.Capture(gate, static () => new object()).State == "available",
            "diagnostic-failure-releases-its-acquired-gate");
        var monitor = new object(); var invoked = false;
        WithHeldDiagnosticGate(body => { lock (monitor) body(); }, () =>
        {
            var sample = NonBlockingDiagnostic<object>.Capture(monitor, () => { invoked = true; return new object(); });
            Check(sample.State == "contended" && sample.Value is null && !invoked,
                "diagnostic-monitor-contention-does-not-invoke-capture");
        });
        var client = SharpClientBuilder.Create().DisableRequestTimeout().UseTcp("127.0.0.1", 1).Build();
        Exception? primary = null;
        try
        {
            var concrete = (SharpLinkClient)client; concrete.EnableContractBindingTraceForDiagnostics();
            var proxy = client.CreateProxy<IProbe>();
            WithHeldDiagnosticGate(concrete.HoldContractSiteIndexForDiagnosticTest, () =>
            {
                var sample = concrete.CaptureContractBindingTrace();
                Check(sample.Sites.State == "contended" && sample.Sites.Value is null && sample.Ledger.DemandSites is null && sample.Ledger.PublishedTargets is null,
                    "actual-site-index-contention-reports-unavailable-not-zero");
                Check(sample.ReadySessions.State == "available" && sample.ReadySessions.Value is { Length: 0 },
                    "diagnostic-uncontended-sections-survive-contested-site-index");
            });
            GC.KeepAlive(proxy);
        }
        catch (Exception exception) { primary = exception; }
        try { await client.StopAsync().ConfigureAwait(false); }
        catch (Exception cleanup) { primary = primary is null ? cleanup : new AggregateException(primary, cleanup); }
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        Check(((SharpLinkClient)client).CaptureContractBindingTrace().StopPhase == "Stopped",
            "actual-client-cleanup-continues-after-contended-snapshot");

        var supervisor = new FrameworkTaskSupervisor();
        using var held = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.Seal();
        supervisor.Track(source.Task, "pure-supervisor-contention", TaskObservationMode.FrameworkOwned, _ =>
        {
            held.Set(); if (!release.Wait(Guard)) throw new TimeoutException("component holder guard");
            return true;
        });
        Exception? completionFailure = null;
        var completion = new Thread(() => { try { source.TrySetException(new InvalidOperationException("expected sealed task")); } catch (Exception error) { completionFailure = error; } }) { IsBackground = true };
        completion.Start();
        try
        {
            if (!held.Wait(Guard)) throw new TimeoutException("component supervisor gate was not entered");
            Check(supervisor.CaptureSnapshotNonBlocking().State == "contended" && !release.IsSet,
                "actual-supervisor-contention-returns-before-owner-release");
        }
        catch (Exception exception) { primary = exception; }
        finally
        {
            release.Set();
            if (!completion.Join(Guard)) primary = primary is null ? new TimeoutException("component completion join") : new AggregateException(primary, new TimeoutException("component completion join"));
        }
        // The RCAA continuation may be scheduled separately; join its actual supervisor lifetime.
        try { await supervisor.DrainAsync().GuardedWaitAsync(Guard).ConfigureAwait(false); }
        catch (Exception cleanup) { primary = primary is null ? cleanup : new AggregateException(primary, cleanup); }
        if (completionFailure is not null) primary = primary is null ? completionFailure : new AggregateException(primary, completionFailure);
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        Check(supervisor.CaptureSnapshotNonBlocking().Value?.IsDrained == true, "supervisor-cleanup-continues-without-abandoned-capture-task");
    }

    private static void WithHeldDiagnosticGate(Action<Action> hold, Action capture)
    {
        using var held = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var exited = new ManualResetEventSlim(); Exception? primary = null, holderFailure = null;
        var holder = new Thread(() =>
        {
            try { hold(() => { held.Set(); if (!release.Wait(Guard)) throw new TimeoutException("component holder guard"); }); }
            catch (Exception failure) { holderFailure = failure; }
            finally { exited.Set(); }
        }) { IsBackground = true };
        holder.Start();
        try
        {
            if (!held.Wait(Guard)) throw new TimeoutException("component gate was not entered");
            capture();
            Check(!exited.IsSet, "diagnostic-returned-while-real-gate-owner-still-held");
        }
        catch (Exception failure) { primary = failure; }
        finally
        {
            release.Set();
            if (!holder.Join(Guard)) primary = primary is null ? new TimeoutException("component holder join") : new AggregateException(primary, new TimeoutException("component holder join"));
        }
        if (holderFailure is not null) primary = primary is null ? holderFailure : new AggregateException(primary, holderFailure);
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
    }
}
