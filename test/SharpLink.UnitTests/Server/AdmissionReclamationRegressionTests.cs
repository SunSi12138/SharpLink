using System.Runtime.CompilerServices;
using System.Threading;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionReclamationRegressionTests
{
    [Test]
    public async Task KernelDisposeShouldWaitUntilFinalReclaimedStateIsDisposed()
    {
        var kernel = new AdmissionStateKernel(TimeProvider.System);
        using var disposalEntered = new ManualResetEventSlim();
        using var allowDisposal = new ManualResetEventSlim();
        Task? disposeTask = null;
        Task? releaseTask = null;

        try
        {
            var program = CreateProgram(kernel, options => options.Global.UseConcurrency(1));
            Ensure(program.TryAcquireUse(), "test must hold one generation use before retirement");
            Ensure(program.Retire(), "test retirement must win exactly once");
            Ensure(!program.IsReclaimed && program.ActiveUses == 1,
                "active retired use must defer reclamation");

            kernel.BeforeReclaimedStateDisposalForTests = () =>
            {
                disposalEntered.Set();
                if (!allowDisposal.Wait(TimeSpan.FromSeconds(5)))
                    throw new Exception("assert failed: timed out waiting to release reclaimed-state disposal");
            };

            disposeTask = kernel.DisposeAsync().AsTask();
            releaseTask = Task.Run(program.ReleaseUse);

            Ensure(disposalEntered.Wait(TimeSpan.FromSeconds(5)),
                "last release must reach deterministic reclaimed-state disposal probe");
            Ensure(kernel.LiveProgramCount == 0 && kernel.RuleStateCount == 0,
                "registry entries may already be detached while physical state disposal is blocked");
            Ensure(!program.IsReclaimed,
                "program must not report reclaimed before detached state is physically disposed");
            Ensure(!disposeTask.IsCompleted,
                "kernel Dispose must not complete while final reclaimed-state disposal is blocked");

            allowDisposal.Set();
            await releaseTask.WaitAsync(TimeSpan.FromSeconds(5));
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

            Ensure(program.IsReclaimed && program.ReclaimCount == 1,
                "reclamation completes exactly once only after state disposal finishes");
            Ensure(kernel.LiveProgramCount == 0 && kernel.RetiredProgramCount == 0 &&
                   kernel.RuleStateCount == 0 && kernel.PartitionStateCount == 0,
                "kernel must be fully drained after final state disposal completes");
        }
        finally
        {
            allowDisposal.Set();
            kernel.BeforeReclaimedStateDisposalForTests = null;
            if (releaseTask is not null)
                await ObserveTerminalAsync(releaseTask);
            if (disposeTask is not null)
                await ObserveTerminalAsync(disposeTask);
            else
                await kernel.DisposeAsync();
        }
    }

    [Test]
    public async Task ReclaimedInitialControllerOwnerShouldNotRootOldProgramOrState()
    {
        var roots = CreateReclaimedReplacementScenario();
        try
        {
            ForceFullCollection();

            Ensure(!roots.OldProgram.TryGetTarget(out _),
                "server-lifecycle controller root must not retain the reclaimed initial program");
            Ensure(!roots.OldState.TryGetTarget(out _),
                "server-lifecycle controller root must not retain the reclaimed initial limiter state");
            Ensure(roots.LifecycleOwner.Program is null,
                "reclamation must sever the controller-to-program back-reference");
            Ensure(roots.Kernel.LiveProgramCount == 1 && roots.Kernel.RetiredProgramCount == 0 &&
                   roots.Kernel.RuleStateCount == 1,
                "only the incompatible replacement generation/state may remain registered");

            GC.KeepAlive(roots.LifecycleOwner);
            GC.KeepAlive(roots.Replacement);
            GC.KeepAlive(roots.Kernel);
        }
        finally
        {
            roots.Replacement.Retire();
            await roots.Kernel.DisposeAsync();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ReclaimedReplacementRoots CreateReclaimedReplacementScenario()
    {
        var kernel = new AdmissionStateKernel(TimeProvider.System);
        var original = CreateProgram(kernel, options => options.Global.UseConcurrency(1));
        var lifecycleOwner = original.Controller;
        var oldState = lifecycleOwner.GlobalStateForTests ??
            throw new Exception("assert failed: initial global state was not created");
        var replacement = CreateProgram(kernel, options => options.Global.UseConcurrency(2));
        var oldProgram = new WeakReference<AdmissionProgram>(original);
        var oldStateReference = new WeakReference<AdmissionRuleRuntime>(oldState);

        Ensure(original.Retire(), "replacement must retire the initial generation");
        Ensure(original.IsReclaimed && original.ReclaimCount == 1,
            "initial generation must synchronously reclaim when it has no active uses");
        Ensure(lifecycleOwner.Program is null,
            "reclaim must detach the lifecycle owner's initial-program back-reference");
        Ensure(kernel.LiveProgramCount == 1 && kernel.RuleStateCount == 1,
            "incompatible replacement must be the only remaining program/state entry");

        return new ReclaimedReplacementRoots(
            kernel,
            lifecycleOwner,
            replacement,
            oldProgram,
            oldStateReference);
    }

    private static AdmissionProgram CreateProgram(
        AdmissionStateKernel kernel,
        Action<SharpLinkAdmissionControlOptions> configure)
    {
        var options = new SharpLinkAdmissionControlOptions();
        configure(options);
        options.Validate();
        return kernel.CreateProgram(options, []);
    }

    private static void ForceFullCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static async Task ObserveTerminalAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
        }
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }

    private readonly record struct ReclaimedReplacementRoots(
        AdmissionStateKernel Kernel,
        SharpLinkAdmissionController LifecycleOwner,
        AdmissionProgram Replacement,
        WeakReference<AdmissionProgram> OldProgram,
        WeakReference<AdmissionRuleRuntime> OldState);
}
