using System.IO.Pipelines;

namespace SharpLink.UnitTests.Runtime;

public sealed class MaintenanceDrainTransitionTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DrainingWinsEitherOrderedResumeTransition(bool resumeFirst)
    {
        await using var session = CreateSession();
        session.PauseForMaintenance();
        if (resumeFirst) session.ResumeFromMaintenance();
        session.MarkDraining();
        if (!resumeFirst)
        {
            var rejected = false;
            try { session.ResumeFromMaintenance(); }
            catch (SharpLinkException) { rejected = true; }
            Ensure(rejected, "resume cannot resurrect a draining session");
        }
        Ensure(session.ProtocolPhase == RpcSessionProtocolPhase.Draining, "drain wins both lifecycle orderings");
    }

    [Test]
    public async Task DrainingWinsConcurrentResumeWithoutLeavingReady()
    {
        for (var attempt = 0; attempt < 256; attempt++)
        {
            await using var session = CreateSession();
            session.PauseForMaintenance();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = Task.Run(async () =>
            {
                await start.Task;
                try { session.ResumeFromMaintenance(); }
                catch (SharpLinkException) { }
            });
            var drain = Task.Run(async () => { await start.Task; session.MarkDraining(); });
            start.SetResult();
            await Task.WhenAll(resume, drain);
            Ensure(session.ProtocolPhase == RpcSessionProtocolPhase.Draining,
                "a failed paused CAS must retry a concurrently resumed Ready snapshot");
        }
    }

    private static RpcSession CreateSession()
        => RpcSessionTestFixture.CreateSessionOverTestTransport("maintenance-drain-race",
            new Pipe().Reader, new Pipe().Writer, RpcSessionTestFixture.ClientOptions());
    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
