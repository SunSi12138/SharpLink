using System;
using System.Threading.Tasks;

namespace SharpLink.UnitTests.Runtime;

public sealed class FrameworkTaskSupervisorDetachedTests
{
    [Test]
    public async Task CompletedSuccessMustPreserveAccountingAndDrainRules()
    {
        var supervisor = new FrameworkTaskSupervisor();
        for (var index = 0; index < 10; index++)
            supervisor.TrackDetached(Task.CompletedTask, "success", TaskObservationMode.FrameworkOwned, static _ => false);
        supervisor.Seal();
        supervisor.TrackDetached(Task.CompletedTask, "late", TaskObservationMode.FrameworkOwned, static _ => false);
        await supervisor.DrainAsync();
        var snapshot = supervisor.CaptureSnapshot();
        Ensure(snapshot.TotalTracked == 11 && snapshot.LateRegistrations == 1 && snapshot.IsDrained,
            "detached synchronous success must count each logical registration");
        try
        {
            supervisor.TrackDetached(Task.CompletedTask, "rejected", TaskObservationMode.FrameworkOwned, static _ => false);
            throw new Exception("expected rejection");
        }
        catch (InvalidOperationException) { }
        Ensure(supervisor.CaptureSnapshot().RejectedRegistrations == 1, "completed success cannot bypass drained gate");
    }

    [Test]
    public async Task FaultCancelAndPendingMustKeepGeneralTaskOwnership()
    {
        foreach (var canceled in new[] { false, true })
        {
            var supervisor = new FrameworkTaskSupervisor();
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            supervisor.TrackDetached(source.Task, "pending", TaskObservationMode.FrameworkOwned, static _ => false);
            supervisor.Seal();
            var drain = supervisor.DrainAsync();
            Ensure(!drain.IsCompleted, "pending task must hold drain");
            if (canceled) source.SetCanceled();
            else source.SetException(new InvalidOperationException("marker"));
            Exception? failure = null;
            try { await drain; }
            catch (Exception exception) { failure = exception; }
            Ensure(canceled ? failure is TaskCanceledException : failure is InvalidOperationException,
                "cancel/fault distinction must not take success fastpath");
            Ensure(supervisor.CaptureSnapshot().IsDrained, "terminal task must retire");
        }
    }

    [Test]
    public void CompletedSuccessMustStillValidateArguments()
    {
        var supervisor = new FrameworkTaskSupervisor();
        var caught = 0;
        try { supervisor.TrackDetached(null!, "x", TaskObservationMode.FrameworkOwned, static _ => false); }
        catch (ArgumentNullException) { caught++; }
        try { supervisor.TrackDetached(Task.CompletedTask, " ", TaskObservationMode.FrameworkOwned, static _ => false); }
        catch (ArgumentException) { caught++; }
        try { supervisor.TrackDetached(Task.CompletedTask, "x", (TaskObservationMode)99, static _ => false); }
        catch (ArgumentOutOfRangeException) { caught++; }
        try { supervisor.TrackDetached(Task.CompletedTask, "x", TaskObservationMode.FrameworkOwned, null!); }
        catch (ArgumentNullException) { caught++; }
        Ensure(caught == 4 && supervisor.CaptureSnapshot().TotalTracked == 0, "all validation precedes accounting");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
