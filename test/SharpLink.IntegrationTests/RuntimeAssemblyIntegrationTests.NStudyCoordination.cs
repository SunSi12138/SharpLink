using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp", "client")]
    [Arguments("tcp", "server")]
    [Arguments("shared-memory", "client")]
    [Arguments("shared-memory", "server")]
    public Task NStudyManualCoordinationUsesActualPreparedGroupOnTheSameSession(string transport, string initiator)
        => VerifyNStudyManualCoordinationAsync(transport, initiator, faultingPolicy: null);

    [Test]
    [NotInParallel]
    [Arguments("tcp", "client")]
    [Arguments("tcp", "server")]
    [Arguments("shared-memory", "client")]
    [Arguments("shared-memory", "server")]
    public Task NStudyApplicationApprovalFailureShouldLeaveOriginalSessionUsable(string transport, string faultingPolicy)
        => VerifyNStudyManualCoordinationAsync(transport, faultingPolicy, faultingPolicy);

    [Test]
    [NotInParallel]
    [Arguments("tcp", "client")]
    [Arguments("tcp", "server")]
    [Arguments("shared-memory", "client")]
    [Arguments("shared-memory", "server")]
    public Task NStudyApprovalDeadlineShouldNotCloseOriginalSession(string transport, string waitingPolicy)
        => VerifyNStudyManualCoordinationAsync(transport, waitingPolicy, waitingPolicy, expirePolicy: true);

    private static async Task VerifyNStudyManualCoordinationAsync(
        string transport, string initiator, string? faultingPolicy, bool expirePolicy = false)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = guard.Token;
        var choice = initiator == "client" ? NStudyInitiator.Client : NStudyInitiator.Server;
        var trace = new NStudyTrace(transport);
        using var contracts = PluginBundle.Load($"n-manual-{transport}-{initiator}", loadService: false);
        var staticLoad = SharpLinkAssemblyManifestLoader.TryLoad(
            typeof(IShutdownCleanupProbe).Assembly, out var staticManifest);
        var dynamicLoad = SharpLinkAssemblyManifestLoader.TryLoad(contracts.ContractAssembly, out var manifest);
        Ensure(staticLoad.Succeeded && dynamicLoad.Succeeded, "actual compatible generated manifests");
        var source = new FixedGeneratedManifestSource([staticManifest!]);
        var bCid = staticManifest!.Contracts.Single(c => c.ContractType == typeof(IShutdownCleanupProbe)).ContractId;
        var aCid = manifest!.Contracts.Single(c => c.ContractType == contracts.ContractType).ContractId;
        await using var provider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
        var builder = NewNStudyServer(source, provider);
        using var approvalLog = new NStudyApprovalLogger();
        if (faultingPolicy == "server")
            builder.UseLoggerFactory(approvalLog);
        string? memoryName = transport == "shared-memory" ? $"sharplink-n-manual-{Guid.NewGuid():N}" : null;
        if (memoryName is null)
            builder.UseTcp(0, IPAddress.Loopback);
        else
            builder.UseSharedMemory(memoryName);
        var port = memoryName is null ? ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port : 0;
        await using var server = (SharpLinkServer)builder.Build();
        await using var client = NewNStudyClient(source, port, memoryName,
            faultingPolicy == "client" ? builder => builder.UseLoggerFactory(approvalLog) : null);
        server.NStudyVersionCoordinationMode = client.NStudyVersionCoordinationMode = NStudyCoordinationMode.Manual;
        var path = Path.Combine(Path.GetDirectoryName(contracts.ContractAssembly.Location)!,
            "SharpLink.DynamicPlugin.Services.dll");
        var oldLoader = new SharedContractServiceLoadContext(contracts.ContractAssembly);
        var newLoader = new SharedContractServiceLoadContext(contracts.ContractAssembly);
        Type? oldType = null;
        var serverDecision = new NStudyManualDecision();
        var clientDecision = new NStudyManualDecision();
        try
        {
            var oldAssembly = oldLoader.LoadFromAssemblyPath(path);
            var newAssembly = newLoader.LoadFromAssemblyPath(path);
            oldType = oldAssembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!;
            var newType = newAssembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!;
            Ensure(server.RegisterAssembly(contracts.ContractAssembly).Succeeded &&
                   server.RegisterAssembly(oldAssembly).Succeeded &&
                   client.RegisterAssembly(contracts.ContractAssembly).Succeeded, "only R1 and compatible local contract are registered");
            await server.StartAsync(ct);
            await client.ConnectAsync(ct);
            var (b, bBinding) = await PrepareNStudySiblingAsync(client, bCid, ct);
            var site = CreateNStudyExactGenerated(client, contracts.ContractType);
            var a = site.Service;
            var a1 = await site.PrepareExact(new(bBinding.Authority, aCid, manifest.RpcAssemblyHash,
                server.ReadNStudyTarget(oldAssembly, aCid)), TimeSpan.FromSeconds(5), ct);
            var connection = NStudyClientConnection(client);
            var session = connection.Session;
            var peer = NStudyServerConnection(server);
            var clientContractModule = GetDynamicModule(client, contracts.ContractAssembly);
            var observed = await server.QueryNStudyClientInventoryAsync(peer, ct);
            var observedClientRegistration = observed.Select(aCid);
            Ensure(session.SupportsNStudyCoordination && peer.Session.SupportsNStudyCoordination &&
                   a1.Authority == bBinding.Authority && a1.Authority.PhysicalSession == peer.NStudyPhysicalIdentity &&
                   observed.Authority == a1.Authority && observedClientRegistration.Hash == manifest.RpcAssemblyHash &&
                   observedClientRegistration.Target.Registration == clientContractModule.NStudyRegistration &&
                   peer.AuthenticationContext?.Subject == "n-validation" &&
                   (memoryName is null ? session.RemoteEndPoint is IPEndPoint : session.Input is SharedMemoryPipeReader),
                "both selected authenticated peers negotiated Manual on their real Session");
            trace.Record("ManualCapabilityNegotiatedOnTheBoundPhysicalSession", a1, bBinding);

            if (expirePolicy)
            {
                await VerifyNStudyApprovalDeadlineAsync(server, client, peer, session, choice, faultingPolicy!,
                    oldAssembly, newAssembly, newType, b, a1, bBinding, trace, ct);
                return;
            }

            if (faultingPolicy is not null)
            {
                await VerifyNStudyApplicationApprovalFailureAsync(server, client, peer, session, choice, faultingPolicy,
                    oldAssembly, newAssembly, newType, contracts.ContractType, a, b, a1, bBinding, trace, approvalLog, ct);
                return;
            }

            // Normal application choice: decline a staged version, then allow a new
            // attempt. No business traffic is retargeted by the declaration alone.
            var firstClient = client.NegotiateNStudyReplacementAsync(choice,
                static (_, _) => ValueTask.FromResult(false), ct);
            var firstServer = server.StageNStudyReplacementAsync(peer, oldAssembly, newAssembly, aCid, choice,
                static (_, _) => ValueTask.FromResult(true), TimeSpan.FromSeconds(15), ct);
            Ensure(await firstClient.WaitAsync(ct) is null, "ordinary local manual decline produces no activation");
            var firstOutcome = await firstServer.WaitAsync(ct);
            var declined = firstOutcome.Change ?? throw new InvalidOperationException("The declined candidate was not staged.");
            Ensure(!firstOutcome.Replacement.Succeeded &&
                   server.ReadNStudyTarget(oldAssembly, aCid) == a1.Target &&
                   session.CaptureNStudyBinding(aCid) == a1.BindingId && NStudyCount(newType, "Created") == 0 &&
                   await InvokeValueTaskAsync<int>(a, contracts.ContractType, "UnaryAsync", 6, ct) == 7,
                "decline leaves exact R1 and its original route untouched");
            trace.Record("NormalManualDeclineKeepsTheOriginalBinding", a1);

            var oldCall = InvokeValueTaskAsync<int>(a, contracts.ContractType, "BlockAsync", ct).AsTask();
            await ReadNStudyTask(oldType, "BlockStarted").WaitAsync(ct);
            var replacing = server.StageNStudyReplacementAsync(peer, oldAssembly, newAssembly, aCid, choice,
                async (change, policyToken) =>
                {
                    var snapshot = await server.QueryNStudyClientInventoryAsync(peer, policyToken);
                    var local = snapshot.Select(change.Contract);
                    Ensure(snapshot.Authority == change.Authority && local.Hash == change.Hash &&
                           local.Target.Registration == clientContractModule.NStudyRegistration,
                        "server policy can await the current authenticated peer inventory before approving");
                    return await serverDecision.DecideAsync(change, policyToken);
                }, TimeSpan.FromSeconds(15), ct);
            if (choice == NStudyInitiator.Client)
            {
                Ensure(!serverDecision.Seen.IsCompleted && await b.TouchAsync(1, ct) == 101,
                    "the true server stays at availability until the client elects to propose");
            }
            var negotiating = client.NegotiateNStudyReplacementAsync(choice, async (change, policyToken) =>
            {
                Ensure(await b.TouchAsync(5, policyToken) == 105 &&
                       session.CaptureNStudyBinding(bCid) == bBinding.BindingId,
                    "client policy can await ordinary unrelated RPC on its original prepared route");
                return await clientDecision.DecideAsync(change, policyToken);
            }, ct);
            var offered = await serverDecision.Seen.WaitAsync(ct);
            Ensure(offered.Initiator == choice && offered.Id > declined.Id &&
                   offered.Target != declined.Target && offered.Original == a1.Target &&
                   offered.Contract == aCid && offered.Hash == manifest.RpcAssemblyHash &&
                   offered.Authority == a1.Authority && !clientDecision.Seen.IsCompleted &&
                   server.ReadNStudyTarget(oldAssembly, aCid) == a1.Target && await b.TouchAsync(2, ct) == 102,
                "the actual proposal neither activates R2 nor blocks unrelated B while server policy is pending");
            trace.Record("RealProposalAwaitsServerLocalApproval", a1);

            serverDecision.Approve();
            Ensure(await clientDecision.Seen.WaitAsync(ct) == offered &&
                   server.ReadNStudyTarget(oldAssembly, aCid) == a1.Target &&
                   session.CaptureNStudyBinding(aCid) == a1.BindingId &&
                   await b.TouchAsync(3, ct) == 103 &&
                   await InvokeValueTaskAsync<int>(a, contracts.ContractType, "UnaryAsync", 8, ct) == 9 &&
                   !oldCall.IsCompleted, "server approval alone neither cuts R1 nor opens R2 before client approval");
            trace.Record("BothPoliciesCanAwaitWorkOnTheOriginalSession", a1, bBinding);
            trace.Record("ServerApprovalStillWaitsForPreparedClientDecision", a1);

            var priorBind = session.NStudyBindOperationForDiagnostics;
            clientDecision.Approve();
            var activated = await negotiating.WaitAsync(ct) ?? throw new InvalidOperationException("Expected activation.");
            Ensure(activated.Change == offered &&
                   activated.LocalRegistration == clientContractModule.NStudyRegistration &&
                   activated.Binding.Authority == a1.Authority &&
                   activated.Binding.Target == server.ReadNStudyTarget(newAssembly, aCid) &&
                   activated.Binding.Target == offered.Target &&
                   session.NStudyBindOperationForDiagnostics == priorBind + 1,
                "the new real R2 Bind follows activation of the one approved candidate");
            var a2 = activated.Binding;
            Ensure(!replacing.IsCompleted && !oldCall.IsCompleted &&
                   peer.NStudyBindings.Snapshot() == (2, 1, 1) &&
                   session.CaptureNStudyBinding(bCid) == bBinding.BindingId &&
                   await InvokeValueTaskAsync<int>(a, contracts.ContractType, "UnaryAsync", 10, ct) == 11 &&
                   NStudyCount(newType, "Created") == 1 && await b.TouchAsync(4, ct) == 104 &&
                   session.NStudyBindOperationForDiagnostics == priorBind + 1,
                "new calls reach exact R2 and B without moving the physically active R1 call");
            trace.Record("ApprovedExactR2ResumesWhileOldR1AndBRemainLive", a1, a2);

            oldType.GetMethod("ReleaseBlock")!.Invoke(null, null);
            Ensure(await oldCall.WaitAsync(ct) == 42, "the active original R1 result remains valid");
            var final = await replacing.WaitAsync(ct);
            EnsureReplacementReleased(final.Replacement, "only the old coordinated group retires");
            await WaitForNStudyAsync(() => peer.NStudyBindings.Snapshot() == (2, 0, 0), ct);
            Ensure(final.Change == offered && final.PreparedClientRegistration == activated.LocalRegistration &&
                   final.PreparedClientRegistration == observedClientRegistration.Target.Registration &&
                   NStudyCount(oldType, "Disposed") == 1 && ReferenceEquals(session, NStudyClientSession(client)) &&
                   GetDynamicModule(client, contracts.ContractAssembly) == clientContractModule &&
                   await InvokeValueTaskAsync<int>(a, contracts.ContractType, "UnaryAsync", 12, ct) == 13 &&
                   session.NStudyBindOperationForDiagnostics == priorBind + 1,
                "both participants report the same exact ticket; local client registration/proxy did not get replaced");
            trace.Record("OriginalR1ExitsOnceAndBothReportTheSameActivation", a1, a2);
        }
        finally
        {
            serverDecision.Approve();
            clientDecision.Approve();
            oldType?.GetMethod("ReleaseBlock")!.Invoke(null, null);
            oldLoader.Unload();
            newLoader.Unload();
        }
    }

    private static async Task VerifyNStudyApprovalDeadlineAsync(
        SharpLinkServer server, SharpLinkClient client, ServerConnectionState peer, RpcSession session,
        NStudyInitiator choice, string waitingPolicy, Assembly oldAssembly, Assembly newAssembly,
        Type newType, IShutdownCleanupProbe b, NStudyAck a1, NStudyAck bBinding,
        NStudyTrace trace, CancellationToken ct)
    {
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var originalBindCount = session.NStudyBindOperationForDiagnostics;

        async ValueTask<bool> WaitForApprovalAsync(NStudyChange _, CancellationToken decisionToken)
        {
            entered.SetResult(decisionToken);
            try
            {
                // A normal asynchronous approval task may receive its answer after the caller has stopped waiting.
                return await approval.Task;
            }
            finally
            {
                callbackExited.TrySetResult();
            }
        }

        static ValueTask<bool> ApproveAsync(NStudyChange _, CancellationToken __) => ValueTask.FromResult(true);

        var began = Stopwatch.GetTimestamp();
        var negotiating = client.NegotiateNStudyReplacementAsync(choice,
            waitingPolicy == "client" ? WaitForApprovalAsync : ApproveAsync, ct);
        var replacing = server.StageNStudyReplacementAsync(peer, oldAssembly, newAssembly, a1.Contract, choice,
            waitingPolicy == "server" ? WaitForApprovalAsync : ApproveAsync, TimeSpan.FromSeconds(15), ct);
        try
        {
            var decisionToken = await entered.Task.WaitAsync(ct);
            Ensure(!decisionToken.IsCancellationRequested && !negotiating.IsCompleted && !replacing.IsCompleted &&
                   await b.TouchAsync(1, ct) == 101, "B is usable while the ordinary approval is pending");
            trace.Record("UnrelatedBServesWhileApplicationApprovalIsPending", a1, bBinding);
            NStudyClientActivation? activation = null;
            Exception? error = null;
            try
            {
                activation = await negotiating.WaitAsync(ct);
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                error = exception;
            }
            var outcome = await replacing.WaitAsync(ct);
            var elapsed = Stopwatch.GetElapsedTime(began);
            Ensure(elapsed < TimeSpan.FromSeconds(15) && decisionToken.IsCancellationRequested &&
                   !callbackExited.Task.IsCompleted && activation is null && !outcome.Replacement.Succeeded,
                "actual control deadline settles both peers before late application approval returns");
            trace.Record("BothTransitionOperationsEndBeforeLateApproval", a1, bBinding);

            approval.TrySetResult(true);
            await callbackExited.Task.WaitAsync(ct);
            Ensure(server.ReadNStudyTarget(oldAssembly, a1.Contract) == a1.Target &&
                   session.NStudyBindOperationForDiagnostics == originalBindCount && NStudyCount(newType, "Created") == 0,
                "late approval cannot commit or Bind a staged candidate");
            var originalReady = client.ReadyConnectionCount == 1 &&
                                ReferenceEquals(session, NStudyClientSession(client)) &&
                                !session.LifetimeToken.IsCancellationRequested;
            Console.WriteLine($"Policy deadline: side={waitingPolicy}; elapsed={elapsed.TotalSeconds:F3}s; " +
                              $"clientError={error?.GetType().Name ?? "none"}; serverDecline={outcome.DeclineReason}; " +
                              $"originalReady={originalReady}");
            trace.Record("LateApprovalDoesNotActivateTheCandidate", a1, bBinding);
            Ensure(error is null && originalReady && session.CaptureNStudyBinding(bBinding.Contract) == bBinding.BindingId &&
                   await b.TouchAsync(2, ct) == 102,
                "a local approval timeout must not drop the unrelated prepared B service");
            trace.Record("OriginalBServesAfterApprovalTimeout", a1, bBinding);
        }
        finally
        {
            approval.TrySetResult(false);
        }
    }

    private static async Task VerifyNStudyApplicationApprovalFailureAsync(
        SharpLinkServer server, SharpLinkClient client, ServerConnectionState peer, RpcSession session,
        NStudyInitiator choice, string faultingPolicy, Assembly oldAssembly, Assembly newAssembly, Type newType,
        Type contractType, object a, IShutdownCleanupProbe b, NStudyAck a1, NStudyAck bBinding,
        NStudyTrace trace, NStudyApprovalLogger approvalLog, CancellationToken ct)
    {
        var entered = new TaskCompletionSource<NStudyChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var originalBindCount = session.NStudyBindOperationForDiagnostics;
        var policyFailure = new InvalidOperationException("The application approval check failed.");

        async ValueTask<bool> FailingApprovalAsync(NStudyChange change, CancellationToken decisionToken)
        {
            entered.SetResult(change);
            await release.Task.WaitAsync(decisionToken);
            throw policyFailure;
        }

        static ValueTask<bool> ApproveAsync(NStudyChange _, CancellationToken __) => ValueTask.FromResult(true);

        var negotiating = client.NegotiateNStudyReplacementAsync(choice,
            faultingPolicy == "client" ? FailingApprovalAsync : ApproveAsync, ct);
        var replacing = server.StageNStudyReplacementAsync(peer, oldAssembly, newAssembly, a1.Contract, choice,
            faultingPolicy == "server" ? FailingApprovalAsync : ApproveAsync, TimeSpan.FromSeconds(15), ct);
        try
        {
            var offered = await entered.Task.WaitAsync(ct);
            Ensure(offered.Authority == a1.Authority && offered.Original == a1.Target &&
                   !negotiating.IsCompleted && !replacing.IsCompleted &&
                   await b.TouchAsync(1, ct) == 101 && server.ReadNStudyTarget(oldAssembly, a1.Contract) == a1.Target,
                "unrelated B remains callable while the application approval awaits its own work");
            trace.Record("ApplicationApprovalPendingWhileOriginalBServes", a1, bBinding);

            release.TrySetResult();
            NStudyClientActivation? activation = null;
            Exception? clientError = null;
            try
            {
                activation = await negotiating.WaitAsync(ct);
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                clientError = exception;
            }
            var declined = await replacing.WaitAsync(ct);
            var originalReady = client.ReadyConnectionCount == 1 &&
                                ReferenceEquals(session, NStudyClientSession(client)) &&
                                !session.LifetimeToken.IsCancellationRequested &&
                                ReferenceEquals(peer, NStudyServerConnection(server));
            Console.WriteLine($"Policy failure: side={faultingPolicy}; clientError={clientError?.GetType().Name ?? "none"}; " +
                              $"activation={activation.HasValue}; serverDecline={declined.DeclineReason}; originalReady={originalReady}");
            Ensure(!declined.Replacement.Succeeded && declined.Change == offered && activation is null &&
                   server.ReadNStudyTarget(oldAssembly, a1.Contract) == a1.Target && NStudyCount(newType, "Created") == 0,
                "the faulted application approval must not commit or activate the staged registration");
            Ensure(clientError is null && declined.DeclineReason == NStudyDeclineReason.Policy && originalReady,
                "a local application approval fault must be a policy decline, not a physical Session failure");
            Ensure(approvalLog.Failures.Count(error => ReferenceEquals(error, policyFailure)) == 1,
                "the originating endpoint reports the actual application fault once");
            Ensure(session.CaptureNStudyBinding(a1.Contract) == a1.BindingId &&
                   session.CaptureNStudyBinding(bBinding.Contract) == bBinding.BindingId &&
                   session.NStudyBindOperationForDiagnostics == originalBindCount &&
                   await b.TouchAsync(2, ct) == 102 &&
                   await InvokeValueTaskAsync<int>(a, contractType, "UnaryAsync", 6, ct) == 7,
                "after failure, both original prepared routes still work without reconnecting or binding again");
            trace.Record("ApplicationApprovalFaultDeclinesWithoutLosingOriginalRoutes", a1, bBinding);

            var recoveryClient = client.NegotiateNStudyReplacementAsync(choice, ApproveAsync, ct);
            var recoveryServer = server.StageNStudyReplacementAsync(peer, oldAssembly, newAssembly, a1.Contract, choice,
                ApproveAsync, TimeSpan.FromSeconds(15), ct);
            var recovered = await recoveryClient.WaitAsync(ct) ?? throw new InvalidOperationException("Expected approved recovery.");
            var final = await recoveryServer.WaitAsync(ct);
            EnsureReplacementReleased(final.Replacement, "successful same-Session transition after the application fault");
            Ensure(recovered.Change.Id > offered.Id && recovered.Binding.Authority == a1.Authority &&
                   recovered.Binding.Target == server.ReadNStudyTarget(newAssembly, a1.Contract) &&
                   final.Change == recovered.Change && session.NStudyBindOperationForDiagnostics == originalBindCount + 1 &&
                   ReferenceEquals(session, NStudyClientSession(client)) &&
                   await InvokeValueTaskAsync<int>(a, contractType, "UnaryAsync", 8, ct) == 9 &&
                   await b.TouchAsync(3, ct) == 103 && NStudyCount(newType, "Created") == 1,
                "the next ordinary approved attempt succeeds exactly once on the original Session");
            trace.Record("ApprovedRecoveryAfterApplicationFaultUsesSameSession", recovered.Binding, bBinding);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private sealed class NStudyApprovalLogger : ILoggerFactory, ILogger
    {
        internal readonly ConcurrentQueue<Exception> Failures = new();

        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning && exception is not null)
                Failures.Enqueue(exception);
        }
    }

    private sealed class NStudyManualDecision
    {
        private readonly TaskCompletionSource<NStudyChange> _seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _approval = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<NStudyChange> Seen => _seen.Task;
        internal void Approve() => _approval.TrySetResult(true);
        internal void Decline() => _approval.TrySetResult(false);
        internal async ValueTask<bool> DecideAsync(NStudyChange change, CancellationToken ct)
        {
            _seen.TrySetResult(change);
            return await _approval.Task.WaitAsync(ct);
        }
    }
}
