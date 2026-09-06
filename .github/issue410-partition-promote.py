from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def replace_once(path: str, old: str, new: str) -> None:
    file = ROOT / path
    text = file.read_text()
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected exactly one match, found {count}: {old[:80]!r}")
    file.write_text(text.replace(old, new, 1))


# All FixedWindow scopes, including partition entries, now use the specialized stable counter.
replace_once(
    "src/SharpLink.Server/Admission/AdmissionLimiterState.cs",
    """/// Immutable rate-policy view. Every non-partition FixedWindow uses a stable shared counter;\n/// TokenBucket, SlidingWindow and partition rate state keep the existing #333 implementation.\n/// Algorithm identity changes are generation boundaries rather than history translations.\n""",
    """/// Immutable rate-policy view. Every FixedWindow uses a stable shared counter; TokenBucket and\n/// SlidingWindow keep the existing #333 implementation. Algorithm identity changes are generation\n/// boundaries rather than history translations.\n""",
)
replace_once(
    "src/SharpLink.Server/Admission/AdmissionLimiterState.cs",
    """        var canUseStableFixedWindow =\n            definition.Kind == AdmissionRateStateKind.FixedWindow &&\n            options is not SharpLinkPartitionAdmissionOptions;\n""",
    """        var canUseStableFixedWindow = definition.Kind == AdmissionRateStateKind.FixedWindow;\n""",
)
replace_once(
    "src/SharpLink.Server/Admission/AdmissionLimiterState.cs",
    """        // TokenBucket/SlidingWindow and partitions keep the existing #333 implementation. A source\n        // from the specialized FixedWindow model deliberately starts a fresh algorithm generation.\n""",
    """        // TokenBucket/SlidingWindow keep the existing #333 implementation. A source from the\n        // specialized FixedWindow model deliberately starts a fresh algorithm generation.\n""",
)

# Consolidate rate publication on the controller and include every current partition entry.
replace_once(
    "src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs",
    """    internal void GrantConcurrencyWaitersAfterTargetCommit()\n    {\n        foreach (var binding in _ruleStateBindings)\n            binding.ConcurrencyState?.GrantWaitersAfterTargetCommit();\n        _partitions?.GrantConcurrencyWaitersAfterTargetCommit();\n    }\n\n""",
    """    internal void GrantConcurrencyWaitersAfterTargetCommit()\n    {\n        foreach (var binding in _ruleStateBindings)\n            binding.ConcurrencyState?.GrantWaitersAfterTargetCommit();\n        _partitions?.GrantConcurrencyWaitersAfterTargetCommit();\n    }\n\n    internal void PublishRateTargets()\n    {\n        foreach (var binding in _ruleStateBindings)\n            binding.RateState?.OnPublished();\n        _partitions?.PublishRateTargets();\n    }\n\n""",
)
replace_once(
    "src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs",
    """    internal void GrantConcurrencyWaitersAfterTargetCommit()\n    {\n        lock (_gate)\n        {\n            foreach (var entry in _entries.Values)\n            {\n                foreach (var generation in entry.Generations)\n                    generation.Concurrency?.GrantWaitersAfterTargetCommit();\n            }\n        }\n    }\n\n""",
    """    internal void GrantConcurrencyWaitersAfterTargetCommit()\n    {\n        lock (_gate)\n        {\n            foreach (var entry in _entries.Values)\n            {\n                foreach (var generation in entry.Generations)\n                    generation.Concurrency?.GrantWaitersAfterTargetCommit();\n            }\n        }\n    }\n\n    internal void PublishRateTargets()\n    {\n        lock (_gate)\n        {\n            foreach (var entry in _entries.Values)\n                entry.Current.Rate?.OnPublished();\n        }\n    }\n\n""",
)
replace_once(
    "src/SharpLink.Server/SharpLinkServer.AdmissionProgram.cs",
    "AdmissionRatePublication.PublishTargets(replacement.Controller);",
    "replacement.Controller.PublishRateTargets();",
)
publication = ROOT / "src/SharpLink.Server/Admission/AdmissionRatePublication.cs"
if not publication.exists():
    raise RuntimeError("AdmissionRatePublication.cs unexpectedly missing")
publication.unlink()

# The legacy state is now TokenBucket/SlidingWindow only; remove its dead FixedWindow branches.
path = "src/SharpLink.Server/Admission/AdmissionDynamicRateState.cs"
replace_once(
    path,
    """    private long _tokenTransitionCredit;\n    private long _fixedConsumed;\n    private long _fixedWindowStart;\n    private long _slidingOwnTotal;\n""",
    """    private long _tokenTransitionCredit;\n    private long _slidingOwnTotal;\n""",
)
replace_once(
    path,
    """        if (definition.Kind == AdmissionRateStateKind.None)\n            throw new InvalidOperationException(\"Admission dynamic rate state requires one rate policy.\");\n""",
    """        if (definition.Kind is AdmissionRateStateKind.None or AdmissionRateStateKind.FixedWindow)\n        {\n            throw new InvalidOperationException(\n                \"Legacy dynamic rate state supports TokenBucket or SlidingWindow only.\");\n        }\n""",
)
replace_once(path, "        _fixedWindowStart = now;\n", "")
replace_once(
    path,
    """                case AdmissionRateStateKind.FixedWindow:\n                    CopyTransitionBarrierLocked(source, now);\n                    _fixedConsumed = source._fixedConsumed;\n                    _fixedWindowStart = source._fixedWindowStart;\n                    var targetWindow = GetWindowTimestampTicks();\n                    if (now >= SaturatingAdd(_fixedWindowStart, targetWindow))\n                        _fixedWindowStart = now;\n                    _latestGrantTimestamp = source._latestGrantTimestamp;\n                    break;\n""",
    "",
)
replace_once(
    path,
    """        _tokenTransitionCredit = 0;\n        _fixedConsumed = 0;\n        _fixedWindowStart = now;\n        _slidingOwnTotal = 0;\n""",
    """        _tokenTransitionCredit = 0;\n        _slidingOwnTotal = 0;\n""",
)
replace_once(
    path,
    """            case AdmissionRateStateKind.FixedWindow:\n                _fixedConsumed = SaturatingAdd(_fixedConsumed, 1);\n                break;\n""",
    "",
)
replace_once(
    path,
    """            AdmissionRateStateKind.TokenBucket => _tokenDebt,\n            AdmissionRateStateKind.FixedWindow => _fixedConsumed,\n            AdmissionRateStateKind.SlidingWindow => _slidingOwnTotal,\n""",
    """            AdmissionRateStateKind.TokenBucket => _tokenDebt,\n            AdmissionRateStateKind.SlidingWindow => _slidingOwnTotal,\n""",
)
replace_once(
    path,
    """            case AdmissionRateStateKind.FixedWindow when _fixedConsumed != 0:\n                expiry = Math.Max(\n                    expiry,\n                    SaturatingAdd(_fixedWindowStart, GetWindowTimestampTicks()));\n                break;\n""",
    "",
)
replace_once(
    path,
    """        switch (_definition.Kind)\n        {\n            case AdmissionRateStateKind.FixedWindow:\n                AdvanceFixedWindowLocked(now);\n                break;\n            case AdmissionRateStateKind.SlidingWindow:\n                AdvanceSlidingWindowLocked(now);\n                break;\n        }\n""",
    """        if (_definition.Kind == AdmissionRateStateKind.SlidingWindow)\n            AdvanceSlidingWindowLocked(now);\n""",
)
replace_once(
    path,
    """    private void AdvanceFixedWindowLocked(long now)\n    {\n        var window = GetWindowTimestampTicks();\n        var elapsed = now - _fixedWindowStart;\n        if (elapsed < window)\n            return;\n\n        var windows = elapsed / window;\n        _fixedWindowStart = SaturatingAdd(\n            _fixedWindowStart,\n            SaturatingMultiply(windows, window));\n        _fixedConsumed = 0;\n    }\n\n""",
    "",
)
replace_once(
    path,
    """            case AdmissionRateStateKind.FixedWindow when _fixedConsumed != 0:\n                next = Math.Min(\n                    next,\n                    SaturatingAdd(_fixedWindowStart, GetWindowTimestampTicks()));\n                break;\n""",
    "",
)
replace_once(
    path,
    """            AdmissionRateStateKind.FixedWindow => GetWindowTimestampTicks(),\n            AdmissionRateStateKind.SlidingWindow => GetWindowTimestampTicks(),\n""",
    """            AdmissionRateStateKind.SlidingWindow => GetWindowTimestampTicks(),\n""",
)
legacy = (ROOT / path).read_text()
for dead in ("_fixedConsumed", "_fixedWindowStart", "case AdmissionRateStateKind.FixedWindow"):
    if dead in legacy:
        raise RuntimeError(f"legacy FixedWindow branch survived: {dead}")

# Partition algorithm replacement follows the same generation-boundary contract as Global/Contract/Method.
advanced = "test/SharpLink.UnitTests/Server/AdmissionDynamicPartitionUpdateAdvancedTests.cs"
replace_once(
    advanced,
    """    [Test]\n    [Arguments(PartitionRateKind.TokenBucket, PartitionRateKind.FixedWindow)]\n    [Arguments(PartitionRateKind.FixedWindow, PartitionRateKind.SlidingWindow)]\n    [Arguments(PartitionRateKind.SlidingWindow, PartitionRateKind.TokenBucket)]\n    public async Task PartitionRateAlgorithmReplacementShouldCarryRecentConsumption(\n        PartitionRateKind sourceKind,\n        PartitionRateKind targetKind)\n    {\n        await using var server = CreateServer();\n        var publicServer = (ISharpLinkServer)server;\n        publicServer.EnableAdmissionControl(options =>\n            ConfigurePartitionRate(options, ConnectionSelector, sourceKind, permitLimit: 1));\n        var source = Current(server);\n        var context = Context(\"tenant-a\");\n\n        var consumed = await source.Controller.AcquireAsync(context, 1, false, CancellationToken.None);\n        Ensure(consumed.IsAcquired, $\"{sourceKind}: source quota must be consumed before replacement\");\n        consumed.Lease!.Dispose();\n\n        publicServer.UpdateAdmissionControl(options =>\n            ConfigurePartitionRate(options, ConnectionSelector, targetKind, permitLimit: 1));\n        var replacement = Current(server);\n        var attempt = await replacement.Controller.AcquireAsync(context, 1, false, CancellationToken.None);\n        Ensure(!attempt.IsAcquired && attempt.Reason == \"rate\",\n            $\"{sourceKind}->{targetKind}: replacement must carry a conservative debt barrier rather than a fresh quota\");\n    }\n""",
    """    [Test]\n    [Arguments(PartitionRateKind.TokenBucket, PartitionRateKind.SlidingWindow)]\n    [Arguments(PartitionRateKind.SlidingWindow, PartitionRateKind.TokenBucket)]\n    public async Task LegacyPartitionAlgorithmReplacementShouldCarryRecentConsumption(\n        PartitionRateKind sourceKind,\n        PartitionRateKind targetKind)\n    {\n        await using var server = CreateServer();\n        var publicServer = (ISharpLinkServer)server;\n        publicServer.EnableAdmissionControl(options =>\n            ConfigurePartitionRate(options, ConnectionSelector, sourceKind, permitLimit: 1));\n        var source = Current(server);\n        var context = Context(\"tenant-a\");\n\n        var consumed = await source.Controller.AcquireAsync(context, 1, false, CancellationToken.None);\n        Ensure(consumed.IsAcquired, $\"{sourceKind}: source quota must be consumed before replacement\");\n        consumed.Lease!.Dispose();\n\n        publicServer.UpdateAdmissionControl(options =>\n            ConfigurePartitionRate(options, ConnectionSelector, targetKind, permitLimit: 1));\n        var replacement = Current(server);\n        var attempt = await replacement.Controller.AcquireAsync(context, 1, false, CancellationToken.None);\n        Ensure(!attempt.IsAcquired && attempt.Reason == \"rate\",\n            $\"{sourceKind}->{targetKind}: legacy replacement must keep its conservative debt barrier\");\n    }\n\n    [Test]\n    [Arguments(PartitionRateKind.TokenBucket, PartitionRateKind.FixedWindow)]\n    [Arguments(PartitionRateKind.FixedWindow, PartitionRateKind.TokenBucket)]\n    [Arguments(PartitionRateKind.FixedWindow, PartitionRateKind.SlidingWindow)]\n    [Arguments(PartitionRateKind.SlidingWindow, PartitionRateKind.FixedWindow)]\n    public async Task FixedWindowPartitionAlgorithmReplacementShouldStartFreshGeneration(\n        PartitionRateKind sourceKind,\n        PartitionRateKind targetKind)\n    {\n        await using var server = CreateServer();\n        var publicServer = (ISharpLinkServer)server;\n        publicServer.EnableAdmissionControl(options =>\n            ConfigurePartitionRate(options, ConnectionSelector, sourceKind, permitLimit: 1));\n        var context = Context(\"tenant-a\");\n\n        var consumed = await Current(server).Controller.AcquireAsync(\n            context, 1, false, CancellationToken.None);\n        Ensure(consumed.IsAcquired, $\"{sourceKind}: source quota must be consumed before replacement\");\n        consumed.Lease!.Dispose();\n\n        publicServer.UpdateAdmissionControl(options =>\n            ConfigurePartitionRate(options, ConnectionSelector, targetKind, permitLimit: 1));\n        var replacement = Current(server);\n        var fresh = await replacement.Controller.AcquireAsync(\n            context, 1, false, CancellationToken.None);\n        Ensure(fresh.IsAcquired,\n            $\"{sourceKind}->{targetKind}: FixedWindow boundary must start one fresh target generation\");\n        fresh.Lease!.Dispose();\n        var exhausted = await replacement.Controller.AcquireAsync(\n            context, 1, false, CancellationToken.None);\n        Ensure(!exhausted.IsAcquired && exhausted.Reason == \"rate\",\n            $\"{sourceKind}->{targetKind}: fresh target must still enforce its own one-permit budget\");\n    }\n""",
)

transition_tests = "test/SharpLink.UnitTests/Server/AdmissionDynamicPartitionRateTransitionTests.cs"
replace_once(
    transition_tests,
    """            await EnsureRateRejectedAsync(current,\n                \"cancelling an old partition waiter must not erase quota consumed before update\");\n""",
    """            await ConsumeAsync(current, 1);\n            await EnsureRateRejectedAsync(current,\n                \"the fresh FixedWindow target must enforce its own one-permit budget after old waiter cancellation\");\n""",
)
replace_once(
    transition_tests,
    """    public async Task LateOldPartitionFixedWindowGrantShouldRemainDebtOnTokenBucketTarget()\n""",
    """    public async Task LateOldPartitionFixedWindowGrantShouldNotChargeFreshTokenBucketTarget()\n""",
)
replace_once(
    transition_tests,
    """            current = CommitUpdate(kernel, source, options =>\n            {\n                ConfigureQueue(options);\n                ConfigureTokenBucket(options, 1, 1, 1);\n            });\n\n            time.Advance(TimeSpan.FromSeconds(40));\n            var oldDecision = await oldQueued;\n            Ensure(oldDecision.IsAcquired,\n                \"old partition waiter must remain valid and grant when its captured source window rolls\");\n            oldDecision.Lease!.Dispose();\n            Ensure(kernel.QueuedCalls == 0,\n                \"late old partition grant must release its outer queue reservation exactly once\");\n\n            await EnsureRateRejectedAsync(current,\n                \"target partition lineage must account for the old-generation grant at handoff time\");\n            time.Advance(TimeSpan.FromSeconds(1));\n            await EnsureRateRejectedAsync(current,\n                \"fast target replenishment must not erase debt belonging to the old forty-second window\");\n            time.Advance(TimeSpan.FromSeconds(39).Subtract(TimeSpan.FromTicks(1)));\n            await EnsureRateRejectedAsync(current,\n                \"legacy partition grant debt must remain one tick before conservative expiry\");\n            time.Advance(TimeSpan.FromTicks(1));\n            await ConsumeAsync(current, 1);\n""",
    """            current = CommitUpdate(kernel, source, options =>\n            {\n                ConfigureQueue(options);\n                ConfigureTokenBucket(options, 1, 1, 1);\n            });\n            await ConsumeAsync(current, 1);\n            await EnsureRateRejectedAsync(current,\n                \"the fresh TokenBucket target must initially enforce its own one-token budget\");\n\n            time.Advance(TimeSpan.FromSeconds(40));\n            var oldDecision = await oldQueued;\n            Ensure(oldDecision.IsAcquired,\n                \"old partition waiter must remain valid and grant when its captured source window rolls\");\n            oldDecision.Lease!.Dispose();\n            Ensure(kernel.QueuedCalls == 0,\n                \"late old partition grant must release its outer queue reservation exactly once\");\n\n            await ConsumeAsync(current, 1);\n            await EnsureRateRejectedAsync(current,\n                \"the late old FixedWindow grant must not be translated into debt on the fresh TokenBucket generation\");\n""",
)

# Server-level partition integration: shared accounting snapshots and post-pointer queue activation.
partition_tests = ROOT / "test/SharpLink.UnitTests/Server/DynamicFixedWindowPartitionTests.cs"
if partition_tests.exists():
    raise RuntimeError("DynamicFixedWindowPartitionTests.cs already exists")
partition_tests.write_text(r'''using System.Net;
using System.Threading;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class DynamicFixedWindowPartitionTests
{
    private static readonly Func<SharpLinkAdmissionContext, string?> Selector = static _ => "tenant-a";

    [Test]
    public async Task LimitOnlyPartitionUpdateShouldShareCounterAcrossProgramSnapshots()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => Configure(options, 3));
        var source = Current(server);
        Ensure(source.TryAcquireUse(), "test must pin the old partition policy snapshot");

        try
        {
            await ConsumeAsync(source);
            await ConsumeAsync(source);

            publicServer.UpdateAdmissionControl(options => Configure(options, 1));
            var shrunk = Current(server);
            await EnsureRateRejectedAsync(shrunk,
                "new partition snapshot must see the immediate limit-one target behind consumed=2");

            await ConsumeAsync(source);
            await EnsureRateRejectedAsync(shrunk,
                "old snapshot's third grant must charge the same partition counter seen by the new snapshot");

            publicServer.UpdateAdmissionControl(options => Configure(options, 4));
            var expanded = Current(server);
            await ConsumeAsync(expanded);
            await EnsureRateRejectedAsync(expanded,
                "3 consumed permits followed by 1 -> 4 must expose exactly one additional permit");
        }
        finally
        {
            source.ReleaseUse();
        }
    }

    [Test]
    [NotInParallel]
    public async Task PartitionImmediateIncreaseMustNotWakeQueuedWorkBeforeProgramPublication()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
        {
            Configure(options, 1);
            ConfigureQueue(options);
        });
        var source = Current(server);
        Ensure(source.TryAcquireUse(), "queued old partition request must keep its source snapshot alive");
        using var candidateBuilt = new ManualResetEventSlim();
        using var releaseCandidate = new ManualResetEventSlim();

        try
        {
            await ConsumeAsync(source, allowQueue: true);
            var queued = source.Controller.AcquireAsync(
                Context(), 5, allowQueue: true, CancellationToken.None).AsTask();
            await WaitUntilAsync(
                () => source.Kernel.QueuedCalls == 1 && source.Kernel.QueuedBytes == 5,
                "old partition rate waiter must own one outer queue reservation before update");

            SharpLinkServer.AfterAdmissionCandidateBuiltForTests = (owner, _) =>
            {
                if (!ReferenceEquals(owner, server))
                    return;
                candidateBuilt.Set();
                if (!releaseCandidate.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("partition FixedWindow publication barrier timed out");
            };

            var updateTask = Task.Run(() => publicServer.UpdateAdmissionControl(options =>
            {
                Configure(options, 3);
                ConfigureQueue(options);
            }));
            Ensure(candidateBuilt.Wait(TimeSpan.FromSeconds(5)),
                "partition target must reach the post-build/pre-publication barrier");
            Ensure(!queued.IsCompleted && ReferenceEquals(source, Current(server)),
                "candidate preparation must not expose the larger partition limit before Program publication");

            releaseCandidate.Set();
            await updateTask.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(!ReferenceEquals(source, Current(server)),
                "replacement Program must become current before queued target activation is observed");

            var admitted = await queued.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(admitted.IsAcquired,
                "post-publication partition target must wake the old queued waiter on the shared counter");
            admitted.Lease!.Dispose();
            Ensure(source.Kernel.QueuedCalls == 0 && source.Kernel.QueuedBytes == 0,
                "partition queued continuation must release outer accounting exactly once");
        }
        finally
        {
            SharpLinkServer.AfterAdmissionCandidateBuiltForTests = null;
            releaseCandidate.Set();
            source.ReleaseUse();
        }
    }

    private static SharpLinkServer CreateServer()
        => (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .Build();

    private static AdmissionProgram Current(SharpLinkServer server)
        => server.CurrentAdmissionProgramForTests ??
           throw new Exception("assert failed: expected enabled admission publication");

    private static void Configure(SharpLinkAdmissionControlOptions options, int permitLimit)
        => options.UsePartition(Selector, partition =>
        {
            partition.MaxPartitions = 4;
            partition.IdleTimeout = TimeSpan.FromHours(1);
            partition.UseFixedWindow(rate =>
            {
                rate.PermitLimit = permitLimit;
                rate.Window = TimeSpan.FromHours(1);
            });
        });

    private static void ConfigureQueue(SharpLinkAdmissionControlOptions options)
    {
        options.MaxQueuedCalls = 4;
        options.MaxQueuedBytes = 1024;
        options.MaxQueueDelay = TimeSpan.FromMinutes(1);
    }

    private static async Task ConsumeAsync(AdmissionProgram program, bool allowQueue = false)
    {
        var decision = await program.Controller.AcquireAsync(
            Context(), 1, allowQueue, CancellationToken.None);
        Ensure(decision.IsAcquired, "expected partition FixedWindow permit");
        decision.Lease!.Dispose();
    }

    private static async Task EnsureRateRejectedAsync(AdmissionProgram program, string scenario)
    {
        var decision = await program.Controller.AcquireAsync(
            Context(), 1, allowQueue: false, CancellationToken.None);
        decision.Lease?.Dispose();
        Ensure(!decision.IsAcquired && decision.Reason == "rate" && decision.Scope == "partition", scenario);
    }

    private static SharpLinkAdmissionContext Context()
        => new(101, 202, RpcMethodKind.Unary, "dynamic-fixed-partition", null, null);

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

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }
}
''')

print("issue #410 partition FixedWindow refactor staged")
