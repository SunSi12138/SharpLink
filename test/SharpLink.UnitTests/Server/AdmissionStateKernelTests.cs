using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionStateKernelTests
{
    [Test]
    public async Task PreRetireUseShouldRemainValidAndReclaimExactlyOnceOnLastRelease()
    {
        await using var kernel = new AdmissionStateKernel(TimeProvider.System);
        var program = CreateProgram(kernel, options => options.Global.UseConcurrency(1));

        Ensure(program.TryAcquireUse(), "current generation must acquire its pre-retire use");
        Ensure(program.Retire(), "first retirement must win");
        Ensure(program.IsRetired && !program.IsReclaimed && program.ActiveUses == 1,
            "retirement must preserve the existing use until its terminal release");
        Ensure(!program.TryAcquireUse(), "retired generation must reject every new use");
        Ensure(!program.Retire(), "duplicate retirement must be idempotent");

        program.ReleaseUse();

        Ensure(program.IsReclaimed && program.ReclaimCount == 1,
            "last release must reclaim the retired generation exactly once");
        kernel.TryReclaimProgram(program);
        Ensure(program.ReclaimCount == 1 && kernel.RetiredProgramCount == 0 && kernel.LiveProgramCount == 0,
            "duplicate reclaim attempts must not double-reclaim or retain history");
    }

    [Test]
    public async Task CompatibleGlobalConcurrencyShouldReuseStateAndConstrainNextGeneration()
    {
        await using var kernel = new AdmissionStateKernel(TimeProvider.System);
        var original = CreateProgram(kernel, options => options.Global.UseConcurrency(1));
        var held = await original.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(held.IsAcquired, "generation N must hold the sole global permit");

        var replacement = CreateProgram(kernel, options => options.Global.UseConcurrency(1));
        Ensure(ReferenceEquals(
                original.Controller.GlobalStateForTests,
                replacement.Controller.GlobalStateForTests),
            "identical global concurrency structure must reuse one mutable state object");
        Ensure(kernel.RuleStateCount == 1 && kernel.ActivePermits == 1,
            "compatible overlap must not duplicate limiter state or permit accounting");

        original.Retire();
        var blocked = await replacement.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(!blocked.IsAcquired && blocked.Reason == "concurrency",
            "an active permit acquired under N must constrain compatible N+1");

        held.Lease!.Dispose();
        var admitted = await replacement.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(admitted.IsAcquired, "N+1 must acquire after the shared N permit releases");
        admitted.Lease!.Dispose();
        Ensure(kernel.ActivePermits == 0, "shared active-permit accounting must drain to zero");
        replacement.Retire();
    }

    [Test]
    public async Task CompatibleContractAndMethodRulesShouldReuseStableIdentityState()
    {
        await using var kernel = new AdmissionStateKernel(TimeProvider.System);
        var original = CreateScopedProgram(kernel);
        var replacement = CreateScopedProgram(kernel);

        Ensure(ReferenceEquals(
                original.Controller.ContractStateForTests(101),
                replacement.Controller.ContractStateForTests(101)),
            "contract state identity must be stable contract ID plus limiter structure");
        Ensure(ReferenceEquals(
                original.Controller.MethodStateForTests(101, 202),
                replacement.Controller.MethodStateForTests(101, 202)),
            "method state identity must be stable contract/method IDs plus limiter structure");
        Ensure(kernel.RuleStateCount == 3,
            "global, contract, and method identities must each have one shared state entry");

        original.Retire();
        replacement.Retire();
        Ensure(kernel.RuleStateCount == 0,
            "shared static rule state must be reclaimed when no generation references it");
    }

    [Test]
    public async Task CompatibleRateStateShouldNotResetConsumedQuota()
    {
        await using var kernel = new AdmissionStateKernel(TimeProvider.System);
        var original = CreateProgram(kernel, ConfigureRate);
        var first = await original.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(first.IsAcquired, "generation N must consume the only current rate token");
        first.Lease!.Dispose();

        var replacement = CreateProgram(kernel, ConfigureRate);
        Ensure(ReferenceEquals(
                original.Controller.GlobalStateForTests,
                replacement.Controller.GlobalStateForTests),
            "compatible rate policy must reuse one rate-limiter state object");
        original.Retire();

        var exhausted = await replacement.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(!exhausted.IsAcquired && exhausted.Reason == "rate",
            "publication replacement must not reset already-consumed rate quota");
        replacement.Retire();
    }

    [Test]
    public async Task CompatiblePartitionPolicyShouldReuseNamespaceAndActivePartitionState()
    {
        await using var kernel = new AdmissionStateKernel(TimeProvider.System);
        Func<SharpLinkAdmissionContext, string?> selector = static _ => "tenant-a";
        var original = CreateProgram(kernel, options => ConfigurePartition(options, selector));
        var held = await original.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(held.IsAcquired && original.Controller.ActivePartitions == 1,
            "generation N must materialize one tenant partition");

        var replacement = CreateProgram(kernel, options => ConfigurePartition(options, selector));
        Ensure(ReferenceEquals(
                original.Controller.PartitionStateForTests,
                replacement.Controller.PartitionStateForTests),
            "compatible partition generations must share one namespace/pool");
        Ensure(kernel.PartitionStateCount == 1 && replacement.Controller.ActivePartitions == 1,
            "compatible publication must not duplicate active partition state");
        original.Retire();

        var blocked = await replacement.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(!blocked.IsAcquired && blocked.Reason == "concurrency",
            "partition permit acquired under N must constrain N+1 in the same namespace");
        held.Lease!.Dispose();
        replacement.Retire();
    }

    [Test]
    [Arguments(1, 8, "queue_count")]
    [Arguments(2, 3, "queue_bytes")]
    public async Task OverlappingGenerationsShouldShareQueueBoundsAndRetainedBytes(
        int maxQueuedCalls,
        long maxQueuedBytes,
        string expectedReason)
    {
        await using var kernel = new AdmissionStateKernel(TimeProvider.System);
        var original = CreateProgram(kernel, options => ConfigureQueue(
            options, maxQueuedCalls, maxQueuedBytes));
        var held = await original.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(held.IsAcquired, "generation N must hold the shared concurrency permit");

        var queued = original.Controller.AcquireAsync(
            CreateContext(), retainedBytes: 2, allowQueue: true, CancellationToken.None).AsTask();
        await WaitUntilAsync(() => kernel.QueuedCalls == 1,
            "generation N request enters shared queue accounting");
        Ensure(kernel.QueuedBytes == 2, "queued retained bytes must be owned by the stable kernel");

        var replacement = CreateProgram(kernel, options => ConfigureQueue(
            options, maxQueuedCalls, maxQueuedBytes));
        original.Retire();
        Ensure(original.IsReclaimed,
            "ordinary retirement may reclaim the policy publication while shared state stays alive for N+1");

        var rejected = await replacement.Controller.AcquireAsync(
            CreateContext(), retainedBytes: 2, allowQueue: true, CancellationToken.None);
        Ensure(!rejected.IsAcquired && rejected.Reason == expectedReason,
            "N and N+1 must enforce one server-wide queue count/byte budget");
        Ensure(kernel.QueuedCalls == 1 && kernel.QueuedBytes == 2,
            "rejected N+1 enqueue must not perturb N queue accounting");

        held.Lease!.Dispose();
        var admitted = await queued.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(admitted.IsAcquired, "old-generation queued work must survive ordinary retirement without disposal");
        admitted.Lease!.Dispose();
        await WaitUntilAsync(
            () => kernel.QueuedCalls == 0 && kernel.QueuedBytes == 0 && kernel.ActivePermits == 0,
            "shared queue, retained bytes, and permits drain after old-generation completion");
        replacement.Retire();
    }

    [Test]
    public async Task RepeatedCompatibleGenerationCyclesShouldKeepRegistryBounded()
    {
        await using var kernel = new AdmissionStateKernel(TimeProvider.System);
        var current = CreateProgram(kernel, options => options.Global.UseConcurrency(4));
        var shared = current.Controller.GlobalStateForTests;

        for (var index = 0; index < 64; index++)
        {
            var next = CreateProgram(kernel, options => options.Global.UseConcurrency(4));
            Ensure(ReferenceEquals(shared, next.Controller.GlobalStateForTests),
                "identical republish must keep reusing the original static state entry");
            current.Retire();
            current = next;
            Ensure(kernel.LiveProgramCount == 1 && kernel.RetiredProgramCount == 0,
                "retired generation history must be reclaimed each cycle");
            Ensure(kernel.RuleStateCount == 1,
                "identical republish must not grow the static state registry");
        }

        current.Retire();
        Ensure(kernel.LiveProgramCount == 0 && kernel.RetiredProgramCount == 0 && kernel.RuleStateCount == 0,
            "final retirement must leave no generation history or unreferenced compatible state");
    }

    [Test]
    public async Task IncompatibleStateShouldRemainUntilRetiredUseReleasesThenReclaim()
    {
        await using var kernel = new AdmissionStateKernel(TimeProvider.System);
        var original = CreateProgram(kernel, options => options.Global.UseConcurrency(1));
        Ensure(original.TryAcquireUse(), "test must hold one generation-N use");
        var replacement = CreateProgram(kernel, options => options.Global.UseConcurrency(2));
        Ensure(!ReferenceEquals(
                original.Controller.GlobalStateForTests,
                replacement.Controller.GlobalStateForTests),
            "incompatible limiter structure must not alias mutable state");
        Ensure(kernel.RuleStateCount == 2, "overlapping incompatible structures require two bounded entries");

        original.Retire();
        Ensure(!original.IsReclaimed && kernel.RetiredProgramCount == 1 && kernel.RuleStateCount == 2,
            "retired generation and its incompatible state must stay alive while one use remains");
        original.ReleaseUse();
        Ensure(original.IsReclaimed && kernel.RetiredProgramCount == 0 && kernel.RuleStateCount == 1,
            "last use must reclaim the retired generation and its unreferenced incompatible state");

        replacement.Retire();
        Ensure(kernel.RuleStateCount == 0, "replacement state must eventually reclaim too");
    }

    [Test]
    public async Task EmptyKernelShouldHaveNoProgramOrAccountingState()
    {
        await using var kernel = new AdmissionStateKernel(TimeProvider.System);
        Ensure(kernel.LiveProgramCount == 0 && kernel.RetiredProgramCount == 0,
            "disabled admission must not create generation refcount state");
        Ensure(kernel.RuleStateCount == 0 && kernel.PartitionStateCount == 0,
            "disabled admission must not materialize limiter or partition registry state");
        Ensure(kernel.QueuedCalls == 0 && kernel.QueuedBytes == 0 && kernel.ActivePermits == 0,
            "disabled admission kernel must have zero request accounting");
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

    private static AdmissionProgram CreateScopedProgram(AdmissionStateKernel kernel)
        => CreateProgram(kernel, options =>
        {
            options.Global.UseConcurrency(4);
            options.AddContract(101, rule => rule.UseConcurrency(3));
            options.AddMethod(101, 202, rule => rule.UseConcurrency(2));
        });

    private static void ConfigureRate(SharpLinkAdmissionControlOptions options)
        => options.Global.UseTokenBucket(rate =>
        {
            rate.TokenLimit = 1;
            rate.TokensPerPeriod = 1;
            rate.ReplenishmentPeriod = TimeSpan.FromHours(1);
        });

    private static void ConfigurePartition(
        SharpLinkAdmissionControlOptions options,
        Func<SharpLinkAdmissionContext, string?> selector)
        => options.UsePartition(selector, partition =>
        {
            partition.MaxPartitions = 8;
            partition.IdleTimeout = TimeSpan.FromHours(1);
            partition.UseConcurrency(1);
        });

    private static void ConfigureQueue(
        SharpLinkAdmissionControlOptions options,
        int maxQueuedCalls,
        long maxQueuedBytes)
    {
        options.Global.UseConcurrency(1);
        options.MaxQueuedCalls = maxQueuedCalls;
        options.MaxQueuedBytes = maxQueuedBytes;
        options.MaxQueueDelay = TimeSpan.FromSeconds(5);
    }

    private static SharpLinkAdmissionContext CreateContext()
        => new(101, 202, RpcMethodKind.Unary, "kernel-test", null, null, null);

    private static async Task WaitUntilAsync(Func<bool> condition, string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
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

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }
}
