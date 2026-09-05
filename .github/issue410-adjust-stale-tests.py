from pathlib import Path

ROOT = Path('test/SharpLink.UnitTests/Server')


def replace_test(filename: str, signature: str, replacement: str) -> None:
    path = ROOT / filename
    text = path.read_text(encoding='utf-8')
    sig = text.find(signature)
    if sig < 0:
        raise RuntimeError(f'{filename}: missing signature {signature!r}')
    start = text.rfind('\n    [Test]', 0, sig)
    if start < 0:
        raise RuntimeError(f'{filename}: missing [Test] before {signature!r}')
    start += 1
    next_test = text.find('\n    [Test]', sig + len(signature))
    end = len(text) if next_test < 0 else next_test + 1
    text = text[:start] + replacement.rstrip() + '\n\n' + text[end:]
    path.write_text(text, encoding='utf-8')


replace_test(
    'AdmissionStateKernelTests.cs',
    'public async Task CompatibleRateStateShouldNotResetConsumedQuota()',
    r'''    [Test]
    public async Task IndependentProgramRateStateShouldStartFresh()
    {
        await using var kernel = new AdmissionStateKernel(TimeProvider.System);
        var original = CreateProgram(kernel, ConfigureRate);
        var first = await original.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(first.IsAcquired, "generation N must consume its only current rate token");
        first.Lease!.Dispose();

        var replacement = CreateProgram(kernel, ConfigureRate);
        Ensure(!ReferenceEquals(
                original.Controller.GlobalRateStateForTests,
                replacement.Controller.GlobalRateStateForTests),
            "an independently constructed program generation must own fresh rate-policy state");
        var fresh = await replacement.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(fresh.IsAcquired,
            "generation-scoped rate state must not inherit consumption from an independent program generation");
        fresh.Lease!.Dispose();

        original.Retire();
        replacement.Retire();
    }''')

replace_test(
    'AdmissionDynamicPartitionUpdateAdvancedTests.cs',
    'public async Task PartitionWindowRateIncreaseShouldExposeOnlyDeltaQuota(PartitionRateKind kind)',
    r'''    [Test]
    [Arguments(PartitionRateKind.FixedWindow)]
    [Arguments(PartitionRateKind.SlidingWindow)]
    public async Task PartitionWindowRateIncreaseShouldStartFreshTargetGeneration(PartitionRateKind kind)
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
            ConfigurePartitionRate(options, ConnectionSelector, kind, permitLimit: 1));
        var source = Current(server);
        var pool = source.Controller.PartitionStateForTests!;
        var context = Context("tenant-a");

        var consumed = await source.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        Ensure(consumed.IsAcquired, $"{kind}: source permit must be consumed before update");
        consumed.Lease!.Dispose();

        publicServer.UpdateAdmissionControl(options =>
            ConfigurePartitionRate(options, ConnectionSelector, kind, permitLimit: 2));
        var replacement = Current(server);
        Ensure(ReferenceEquals(pool, replacement.Controller.PartitionStateForTests),
            $"{kind}: structural rate update must preserve the authoritative partition namespace");

        var first = await replacement.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        var second = await replacement.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        var exhausted = await replacement.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        Ensure(first.IsAcquired && second.IsAcquired &&
               !exhausted.IsAcquired && exhausted.Reason == "rate",
            $"{kind}: target generation must start with its own two-permit quota and no migrated source history");
        first.Lease!.Dispose();
        second.Lease!.Dispose();
    }''')

replace_test(
    'AdmissionDynamicPartitionUpdateAdvancedTests.cs',
    'public async Task PartitionRateAlgorithmReplacementShouldCarryRecentConsumption(',
    r'''    [Test]
    [Arguments(PartitionRateKind.TokenBucket, PartitionRateKind.FixedWindow)]
    [Arguments(PartitionRateKind.FixedWindow, PartitionRateKind.SlidingWindow)]
    [Arguments(PartitionRateKind.SlidingWindow, PartitionRateKind.TokenBucket)]
    public async Task PartitionRateAlgorithmReplacementShouldStartFreshTargetGeneration(
        PartitionRateKind sourceKind,
        PartitionRateKind targetKind)
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
            ConfigurePartitionRate(options, ConnectionSelector, sourceKind, permitLimit: 1));
        var source = Current(server);
        var pool = source.Controller.PartitionStateForTests!;
        var context = Context("tenant-a");

        var consumed = await source.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        Ensure(consumed.IsAcquired, $"{sourceKind}: source quota must be consumed before replacement");
        consumed.Lease!.Dispose();

        publicServer.UpdateAdmissionControl(options =>
            ConfigurePartitionRate(options, ConnectionSelector, targetKind, permitLimit: 1));
        var replacement = Current(server);
        Ensure(ReferenceEquals(pool, replacement.Controller.PartitionStateForTests),
            $"{sourceKind}->{targetKind}: algorithm replacement must preserve the partition namespace");
        var first = await replacement.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        var exhausted = await replacement.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        Ensure(first.IsAcquired && !exhausted.IsAcquired && exhausted.Reason == "rate",
            $"{sourceKind}->{targetKind}: target algorithm must start fresh and enforce only its own generation quota");
        first.Lease!.Dispose();
    }''')

replace_test(
    'AdmissionDynamicPartitionUpdateTests.cs',
    'public async Task PartitionTokenBucketIncreaseShouldExposeOnlyDeltaQuota()',
    r'''    [Test]
    public async Task PartitionTokenBucketIncreaseShouldStartFreshTargetGeneration()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
            ConfigurePartitionTokenBucket(options, ConnectionSelector, tokenLimit: 1, tokensPerPeriod: 1));
        var source = Current(server);
        var pool = source.Controller.PartitionStateForTests!;
        var context = Context("tenant-a");

        var consumed = await source.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        Ensure(consumed.IsAcquired, "first request must consume the source partition bucket");
        consumed.Lease!.Dispose();

        publicServer.UpdateAdmissionControl(options =>
            ConfigurePartitionTokenBucket(options, ConnectionSelector, tokenLimit: 2, tokensPerPeriod: 2));
        var replacement = Current(server);
        Ensure(ReferenceEquals(pool, replacement.Controller.PartitionStateForTests),
            "rate parameter update must preserve the same partition namespace");

        var first = await replacement.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        var second = await replacement.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        var exhausted = await replacement.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        Ensure(first.IsAcquired && second.IsAcquired &&
               !exhausted.IsAcquired && exhausted.Reason == "rate",
            "partition target bucket must start with its own two-token generation quota");
        first.Lease!.Dispose();
        second.Lease!.Dispose();
    }''')

replace_test(
    'AdmissionDynamicUpdateTests.cs',
    'public async Task RateTransitionsShouldSucceedWhilePartitionTransitionsRemainTransactional()',
    r'''    [Test]
    public async Task RateGenerationReplacementShouldSucceedWhilePartitionTransitionsRemainTransactional()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => ConfigureRate(
            options, RateKind.TokenBucket, concurrency: 10, rateLimit: 1));
        var source = Current(server);
        var state = source.Controller.GlobalConcurrencyStateForTests!;
        var context = CreateContext();

        var consumed = await source.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        Ensure(consumed.IsAcquired, "source rate permit must be consumed before the public update path is exercised");
        consumed.Lease!.Dispose();

        publicServer.UpdateAdmissionControl(options => ConfigureRate(
            options, RateKind.TokenBucket, concurrency: 5, rateLimit: 2));
        var parameterUpdated = Current(server);
        Ensure(!ReferenceEquals(source, parameterUpdated) &&
               ReferenceEquals(state, parameterUpdated.Controller.GlobalConcurrencyStateForTests) &&
               state.PermitLimit == 5,
            "rate parameter change must publish a fresh rate generation while preserving concurrency continuity");
        var first = await parameterUpdated.Controller.AcquireAsync(
            context, 1, false, CancellationToken.None);
        var second = await parameterUpdated.Controller.AcquireAsync(
            context, 1, false, CancellationToken.None);
        var exhausted = await parameterUpdated.Controller.AcquireAsync(
            context, 1, false, CancellationToken.None);
        Ensure(first.IsAcquired && second.IsAcquired &&
               !exhausted.IsAcquired && exhausted.Reason == "rate",
            "changed TokenBucket target must start with its own two-permit quota");
        first.Lease!.Dispose();
        second.Lease!.Dispose();

        publicServer.UpdateAdmissionControl(options => ConfigureRate(
            options, RateKind.FixedWindow, concurrency: 5, rateLimit: 1));
        var replaced = Current(server);
        var replacementPermit = await replaced.Controller.AcquireAsync(
            context, 1, false, CancellationToken.None);
        var replacementExhausted = await replaced.Controller.AcquireAsync(
            context, 1, false, CancellationToken.None);
        Ensure(replacementPermit.IsAcquired &&
               !replacementExhausted.IsAcquired && replacementExhausted.Reason == "rate",
            "algorithm replacement must start a fresh target generation without weakening its own limit");
        replacementPermit.Lease!.Dispose();

        publicServer.UpdateAdmissionControl(options => options.Global.UseConcurrency(5));
        var removed = Current(server);
        Ensure(removed.Controller.GlobalRateStateForTests is null &&
               ReferenceEquals(state, removed.Controller.GlobalConcurrencyStateForTests),
            "rate removal must publish without replacing the unchanged concurrency state");

        publicServer.UpdateAdmissionControl(options => ConfigureRate(
            options, RateKind.TokenBucket, concurrency: 5, rateLimit: 1));
        var readded = Current(server);
        Ensure(readded.Controller.GlobalRateStateForTests is not null &&
               ReferenceEquals(state, readded.Controller.GlobalConcurrencyStateForTests),
            "rate addition after removal must publish a fresh current component while preserving concurrency");

        publicServer.UpdateAdmissionControl(options =>
        {
            ConfigureRate(options, RateKind.TokenBucket, concurrency: 5, rateLimit: 1);
            options.UsePartition(TenantSelector, partition => partition.UseConcurrency(1));
        });
        var partitionAdded = Current(server);
        Ensure(partitionAdded.Controller.PartitionStateForTests is not null &&
               ReferenceEquals(state, partitionAdded.Controller.GlobalConcurrencyStateForTests),
            "partition addition must publish independently while preserving unchanged non-partition state");
    }''')

replace_test(
    'AdmissionRuntimeControlTests.cs',
    'public async Task PublicReEnableShouldReuseCompatibleConcurrencyRateAndPartitionStateDuringOverlap()',
    r'''    [Test]
    [NotInParallel]
    public async Task PublicReEnableShouldReuseConcurrencyAndPartitionButStartFreshRateDuringOverlap()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(ConfigureRateAndPartition);
        var original = server.CurrentAdmissionProgramForTests!;
        var kernel = original.Kernel;
        Ensure(original.TryAcquireUse(), "test must retain generation N across public disable");

        var originalConcurrency = original.Controller.GlobalConcurrencyStateForTests!;
        var originalRate = original.Controller.GlobalRateStateForTests!;
        var originalPartition = original.Controller.PartitionStateForTests!;
        var first = await original.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(first.IsAcquired, "generation N must consume its concurrency/rate permit and create partition state");

        publicServer.DisableAdmissionControl();
        Ensure(original.IsRetired && !original.IsReclaimed && original.ActiveUses == 1,
            "public disable must retire N without invalidating a captured use");
        publicServer.EnableAdmissionControl(ConfigureRateAndPartition);
        var replacement = server.CurrentAdmissionProgramForTests!;

        Ensure(ReferenceEquals(
                originalConcurrency,
                replacement.Controller.GlobalConcurrencyStateForTests),
            "public re-enable must preserve stable concurrency continuity");
        Ensure(!ReferenceEquals(
                originalRate,
                replacement.Controller.GlobalRateStateForTests),
            "public re-enable must start a fresh rate-policy generation rather than resurrect historical quota");
        Ensure(ReferenceEquals(
                originalPartition,
                replacement.Controller.PartitionStateForTests),
            "compatible public re-enable must reuse the authoritative partition namespace");
        Ensure(kernel.LiveProgramCount == 2 && kernel.RetiredProgramCount == 1 &&
               kernel.ConcurrencyStateCount == 1 && kernel.RateStateCount == 2 &&
               kernel.PartitionStateCount == 1,
            "overlap may duplicate only generation-scoped rate state, not concurrency or partition ownership");

        var blockedByOldPermit = await replacement.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(!blockedByOldPermit.IsAcquired && blockedByOldPermit.Reason == "concurrency",
            "old concurrency permit must constrain the re-enabled generation");

        first.Lease!.Dispose();
        var freshRatePermit = await replacement.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(freshRatePermit.IsAcquired,
            "re-enabled rate generation must have its own fresh quota after shared concurrency becomes available");
        freshRatePermit.Lease!.Dispose();
        var exhausted = await replacement.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(!exhausted.IsAcquired && exhausted.Reason == "rate",
            "fresh re-enabled generation must still enforce its own rate limit");

        original.ReleaseUse();
        Ensure(original.IsReclaimed && original.ReclaimCount == 1 && kernel.RateStateCount == 1,
            "last old-generation use must reclaim historical rate state exactly once");
        publicServer.DisableAdmissionControl();
        AssertDisabledAndEmpty(server, kernel, "overlap cleanup");
    }''')
