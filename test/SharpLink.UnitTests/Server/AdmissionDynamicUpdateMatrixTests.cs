using System.Net;
using System.Reflection;
using System.Threading;
using SharpLink.Server;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionDynamicUpdateMatrixTests
{
    [Test]
    public async Task ContractConcurrencyResizeShouldPreserveActiveHolderState()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
        {
            options.Global.UseConcurrency(16);
            options.AddContract(101, rule => rule.UseConcurrency(1));
        });
        var source = Current(server);
        var contract = source.Controller.ContractConcurrencyStateForTests(101)!;
        var first = await source.Controller.AcquireAsync(
            CreateContext(), 1, false, CancellationToken.None);
        Ensure(first.IsAcquired && contract.ActiveCount == 1,
            "generation N must hold one Contract concurrency permit before resize");

        publicServer.UpdateAdmissionControl(options =>
        {
            options.Global.UseConcurrency(16);
            options.AddContract(101, rule => rule.UseConcurrency(2));
        });
        var replacement = Current(server);
        Ensure(ReferenceEquals(contract, replacement.Controller.ContractConcurrencyStateForTests(101)) &&
               contract.PermitLimit == 2 && contract.ActiveCount == 1,
            "Contract resize must preserve the active holder and exact mutable state");

        var second = await replacement.Controller.AcquireAsync(
            CreateContext(), 1, false, CancellationToken.None);
        var third = await replacement.Controller.AcquireAsync(
            CreateContext(), 1, false, CancellationToken.None);
        Ensure(second.IsAcquired && !third.IsAcquired && third.Reason == "concurrency",
            "Contract increase from one to two may expose exactly one additional permit");
        first.Lease!.Dispose();
        second.Lease!.Dispose();
        Ensure(contract.ActiveCount == 0 && replacement.Controller.ActivePermits == 0,
            "Contract resize holders must release without active-count underflow");
    }

    [Test]
    public async Task MethodConcurrencyResizeShouldPreserveActiveHolderState()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
        {
            options.Global.UseConcurrency(16);
            options.AddMethod(101, 202, rule => rule.UseConcurrency(2));
        });
        var source = Current(server);
        var method = source.Controller.MethodConcurrencyStateForTests(101, 202)!;
        var first = await source.Controller.AcquireAsync(
            CreateContext(), 1, false, CancellationToken.None);
        var second = await source.Controller.AcquireAsync(
            CreateContext(), 1, false, CancellationToken.None);
        Ensure(first.IsAcquired && second.IsAcquired && method.ActiveCount == 2,
            "generation N must hold both Method permits before shrink");

        publicServer.UpdateAdmissionControl(options =>
        {
            options.Global.UseConcurrency(16);
            options.AddMethod(101, 202, rule => rule.UseConcurrency(1));
        });
        var replacement = Current(server);
        Ensure(ReferenceEquals(method, replacement.Controller.MethodConcurrencyStateForTests(101, 202)) &&
               method.PermitLimit == 1 && method.ActiveCount == 2,
            "Method shrink must retain both existing holders on the exact same state");

        var blocked = await replacement.Controller.AcquireAsync(
            CreateContext(), 1, false, CancellationToken.None);
        Ensure(!blocked.IsAcquired && blocked.Reason == "concurrency",
            "Method shrink must create no fresh generation permit budget");
        first.Lease!.Dispose();
        Ensure(method.ActiveCount == 1,
            "Method release down to the target must still leave no spare capacity");
        var stillBlocked = await replacement.Controller.AcquireAsync(
            CreateContext(), 1, false, CancellationToken.None);
        Ensure(!stillBlocked.IsAcquired,
            "active equal to the shrunken Method target must still reject a new holder");
        second.Lease!.Dispose();
        var admitted = await replacement.Controller.AcquireAsync(
            CreateContext(), 1, false, CancellationToken.None);
        Ensure(admitted.IsAcquired && method.ActiveCount == 1,
            "Method capacity must resume naturally only after active falls below target");
        admitted.Lease!.Dispose();
        Ensure(method.ActiveCount == 0 && replacement.Controller.ActivePermits == 0,
            "Method shrink lifecycle must drain exactly");
    }

    [Test]
    public async Task RepeatedResizeWithActiveHolderShouldNotUnderflowOrOvershoot()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => options.Global.UseConcurrency(4));
        var source = Current(server);
        var state = source.Controller.GlobalConcurrencyStateForTests!;
        var holder = await source.Controller.AcquireAsync(
            CreateContext(), 1, false, CancellationToken.None);
        Ensure(holder.IsAcquired && state.ActiveCount == 1,
            "test must retain one active holder throughout repeated resize");

        for (var index = 0; index < 64; index++)
        {
            var target = 1 + index % 4;
            publicServer.UpdateAdmissionControl(options => options.Global.UseConcurrency(target));
            var current = Current(server);
            Ensure(ReferenceEquals(state, current.Controller.GlobalConcurrencyStateForTests) &&
                   state.PermitLimit == target && state.ActiveCount == 1,
                "repeated resize must preserve one state and the pre-existing active holder");

            var transient = await current.Controller.AcquireAsync(
                CreateContext(), 1, false, CancellationToken.None);
            if (target == 1)
            {
                Ensure(!transient.IsAcquired && state.ActiveCount == 1,
                    "target one must not overshoot while the retained holder is active");
            }
            else
            {
                Ensure(transient.IsAcquired && state.ActiveCount == 2,
                    "larger target must expose only legal spare capacity");
                transient.Lease!.Dispose();
                Ensure(state.ActiveCount == 1,
                    "transient release must return exactly to the retained active count");
            }
        }

        holder.Lease!.Dispose();
        Ensure(state.ActiveCount == 0 && Current(server).Controller.ActivePermits == 0,
            "repeated resize must finish without active-count underflow");
    }

    [Test]
    public async Task ReaddedConcurrencyDuringRemovedGenerationOverlapShouldUseFreshState()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
        {
            options.Global.UseConcurrency(8);
            options.AddContract(101, rule => rule.UseConcurrency(1));
        });
        var original = Current(server);
        var oldContract = original.Controller.ContractConcurrencyStateForTests(101)!;
        var kernel = original.Kernel;
        Ensure(original.TryAcquireUse(),
            "test must retain generation N after its Contract component is removed");

        publicServer.UpdateAdmissionControl(options => options.Global.UseConcurrency(8));
        var withoutContract = Current(server);
        Ensure(withoutContract.Controller.ContractConcurrencyStateForTests(101) is null &&
               original.IsRetired && !original.IsReclaimed && kernel.ConcurrencyStateCount == 2,
            "removed Contract state must remain alive only because generation N is still captured");

        publicServer.UpdateAdmissionControl(options =>
        {
            options.Global.UseConcurrency(8);
            options.AddContract(101, rule => rule.UseConcurrency(1));
        });
        var readded = Current(server);
        var newContract = readded.Controller.ContractConcurrencyStateForTests(101)!;
        Ensure(!ReferenceEquals(oldContract, newContract),
            "a newly added Contract component must not attach to a lingering removed-generation state");
        Ensure(kernel.ConcurrencyStateCount == 3,
            "overlap must contain one shared Global plus distinct old and new Contract states");

        original.ReleaseUse();
        Ensure(original.IsReclaimed && kernel.ConcurrencyStateCount == 2,
            "last generation-N use must reclaim only the removed old Contract state");

        var admitted = await readded.Controller.AcquireAsync(
            CreateContext(), 1, false, CancellationToken.None);
        Ensure(admitted.IsAcquired && newContract.ActiveCount == 1,
            "freshly re-added Contract state must remain usable after old-state reclamation");
        admitted.Lease!.Dispose();
    }

    [Test]
    public async Task InvalidCompleteCandidateShouldNotMutateCurrentState()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => options.Global.UseConcurrency(5));
        var source = Current(server);
        var state = source.Controller.GlobalConcurrencyStateForTests!;
        var kernel = source.Kernel;

        var failure = CaptureFailure(() => publicServer.UpdateAdmissionControl(_ => { }));

        Ensure(failure is InvalidOperationException && ReferenceEquals(source, Current(server)),
            "invalid complete candidate must fail before publication and leave current generation unchanged");
        Ensure(state.PermitLimit == 5 && kernel.LiveProgramCount == 1 &&
               kernel.RetiredProgramCount == 0 && kernel.ConcurrencyStateCount == 1 &&
               kernel.RateStateCount == 0 && kernel.PartitionStateCount == 0,
            "invalid update must not mutate live limits or retain speculative state");
        var admitted = await source.Controller.AcquireAsync(
            CreateContext(), 1, false, CancellationToken.None);
        Ensure(admitted.IsAcquired,
            "current program must remain operational after invalid candidate rejection");
        admitted.Lease!.Dispose();
    }

    [Test]
    public async Task UpdateShouldRequireEnabledSupportedServer()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        Ensure(CaptureFailure(() => publicServer.UpdateAdmissionControl(
                options => options.Global.UseConcurrency(1))) is InvalidOperationException,
            "Update must reject while Admission is disabled");

        publicServer.EnableAdmissionControl(options => options.Global.UseConcurrency(1));
        publicServer.DisableAdmissionControl();
        Ensure(CaptureFailure(() => publicServer.UpdateAdmissionControl(
                options => options.Global.UseConcurrency(2))) is InvalidOperationException,
            "Disable then Update must reject rather than implicitly re-enable Admission");

        ISharpLinkServer unsupported = new UnsupportedServer();
        Ensure(CaptureFailure(() => unsupported.UpdateAdmissionControl(
                options => options.Global.UseConcurrency(1))) is NotSupportedException,
            "custom server without runtime-control support must reject Update with NotSupportedException");
    }

    private static SharpLinkServer CreateServer()
    {
        var builder = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback.ToString());
        return (SharpLinkServer)builder.Build();
    }

    private static AdmissionProgram Current(SharpLinkServer server)
        => server.CurrentAdmissionProgramForTests ??
            throw new Exception("assert failed: expected enabled admission publication");

    private static SharpLinkAdmissionContext CreateContext()
        => new(101, 202, RpcMethodKind.Unary, "dynamic-update-matrix", null, null, null);

    private static Exception? CaptureFailure(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }

    private sealed class UnsupportedServer : ISharpLinkServer
    {
        public SharpLinkHealthStatus HealthStatus => default;

        public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly)
            => throw new NotSupportedException();

        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
            Assembly assembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
            Assembly oldAssembly,
            Assembly newAssembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask RunAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask StopAsync(
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
