using System.Net;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

[NotInParallel]
public sealed class SharpLinkServerRuntimeConfigurationResultTests
{
    [Test]
    public async Task CallCapacityTryPathShouldPreservePublicationOnValidationAndLifecycleRejection()
    {
        await using var server = CreateCapacityServer(2, 4);
        var publicServer = (ISharpLinkServer)server;

        var published = publicServer.TryUpdateCallCapacity(3, 6);
        Ensure(published.Succeeded,
            "valid call-capacity candidate should publish through the structured path");
        Ensure(server.MaxConcurrentCallsPerConnectionForDiagnostics == 3 &&
               server.MaxConcurrentCallsPerServerForDiagnostics == 6,
            "successful structured update should publish both capacity fields atomically");

        var invalid = CaptureException(() => publicServer.TryUpdateCallCapacity(0, 8));
        Ensure(invalid is ArgumentOutOfRangeException,
            "invalid capacity remains a parameter exception");
        Ensure(server.MaxConcurrentCallsPerConnectionForDiagnostics == 3 &&
               server.MaxConcurrentCallsPerServerForDiagnostics == 6,
            "invalid candidate must publish nothing");

        await server.StopAsync(TimeSpan.Zero);
        var rejected = publicServer.TryUpdateCallCapacity(4, 8);
        Ensure(!rejected.Succeeded &&
               rejected.FailureCode == SharpLinkRuntimeConfigurationUpdateFailureCode.LifecycleClosed,
            "post-Stop capacity publication should be a structured lifecycle rejection");
        Ensure(server.MaxConcurrentCallsPerConnectionForDiagnostics == 3 &&
               server.MaxConcurrentCallsPerServerForDiagnostics == 6,
            "lifecycle rejection must not mutate call-capacity state");
    }

    [Test]
    public async Task AdmissionModeCheckShouldYieldToClosedLifecycle()
    {
        await using var server = CreateAdmissionServer();
        var publicServer = (ISharpLinkServer)server;

        var modeConflict = publicServer.TryUpdateAdmissionControl(options =>
            options.Global.UseConcurrency(1));
        Ensure(!modeConflict.Succeeded &&
               modeConflict.FailureCode == SharpLinkRuntimeConfigurationUpdateFailureCode.ModeConflict,
            "disabled admission should report a mode conflict while the lifecycle is open");

        await server.StopAsync(TimeSpan.Zero);
        var lifecycleClosed = publicServer.TryUpdateAdmissionControl(options =>
            options.Global.UseConcurrency(1));
        Ensure(!lifecycleClosed.Succeeded &&
               lifecycleClosed.FailureCode == SharpLinkRuntimeConfigurationUpdateFailureCode.LifecycleClosed,
            "after Stop, lifecycle seal should take precedence over admission mode conflict");
    }

    [Test]
    public async Task AdmissionUpdateRacingStopShouldReturnLifecycleClosedAndNeverPublishCandidate()
    {
        await using var server = CreateAdmissionServer();
        var publicServer = (ISharpLinkServer)server;
        var enabled = publicServer.TryEnableAdmissionControl(options =>
            options.Global.UseConcurrency(1));
        Ensure(enabled.Succeeded, "test requires an enabled admission generation");
        var original = server.CurrentAdmissionProgramForTests
            ?? throw new Exception("enabled admission program was not published");
        var kernel = original.Kernel;
        using var candidateBuilt = new ManualResetEventSlim();
        using var releaseCandidate = new ManualResetEventSlim();
        AdmissionProgram? updateCandidate = null;

        try
        {
            SharpLinkServer.AfterAdmissionCandidateBuiltForTests = (owner, candidate) =>
            {
                if (!ReferenceEquals(owner, server))
                    return;
                updateCandidate = candidate;
                candidateBuilt.Set();
                if (!releaseCandidate.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("structured admission update release timed out");
            };

            var updateTask = Task.Run(() => publicServer.TryUpdateAdmissionControl(options =>
                options.Global.UseConcurrency(2)));
            Ensure(candidateBuilt.Wait(TimeSpan.FromSeconds(5)),
                "TryUpdateAdmissionControl must reach the deterministic candidate barrier");

            var stopTask = server.StopAsync(TimeSpan.Zero).AsTask();
            await WaitUntilAsync(() => kernel.IsDraining,
                "Stop must seal admission before the prepared update resumes");
            releaseCandidate.Set();

            var result = await updateTask.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(!result.Succeeded &&
                   result.FailureCode == SharpLinkRuntimeConfigurationUpdateFailureCode.LifecycleClosed,
                "update linearized after Stop seal must return LifecycleClosed");
            Ensure(updateCandidate is { IsRetired: true },
                "rejected prepared candidate must be retired rather than published");
            Ensure(!ReferenceEquals(server.CurrentAdmissionProgramForTests, updateCandidate),
                "rejected candidate must never become the active publication");

            await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            SharpLinkServer.AfterAdmissionCandidateBuiltForTests = null;
            releaseCandidate.Set();
        }
    }

    private static SharpLinkServer CreateCapacityServer(
        int maxConcurrentCallsPerConnection,
        int maxConcurrentCallsPerServer)
        => (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseRuntime(options =>
            {
                options.FlowControl.MaxConcurrentCallsPerConnection = maxConcurrentCallsPerConnection;
                options.FlowControl.MaxConcurrentCallsPerServer = maxConcurrentCallsPerServer;
            })
            .UseTransport(new IdleListener())
            .Build();

    private static SharpLinkServer CreateAdmissionServer()
        => (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .Build();

    private static Exception CaptureException(Action action)
    {
        try
        {
            action();
            throw new Exception("expected exception");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (!condition())
                await Task.Delay(10, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new Exception($"assert failed: {scenario}");
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception($"assert failed: {message}");
    }

    private sealed class IdleListener : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => null;

        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
