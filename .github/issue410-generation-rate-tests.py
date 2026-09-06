from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def remove_test_method(path: str, name: str) -> None:
    file = ROOT / path
    text = file.read_text()
    needle = name + "("
    idx = text.find(needle)
    if idx < 0:
        raise RuntimeError(f"{path}: method not found: {name}")
    start = text.rfind("    [Test]", 0, idx)
    if start < 0:
        raise RuntimeError(f"{path}: [Test] not found before {name}")
    brace = text.find("{", idx)
    if brace < 0:
        raise RuntimeError(f"{path}: body not found: {name}")
    depth = 0
    pos = brace
    in_string = False
    verbatim = False
    escaped = False
    while pos < len(text):
        ch = text[pos]
        if in_string:
            if verbatim:
                if ch == '"':
                    if pos + 1 < len(text) and text[pos + 1] == '"':
                        pos += 2
                        continue
                    in_string = False
                    verbatim = False
            else:
                if escaped:
                    escaped = False
                elif ch == '\\':
                    escaped = True
                elif ch == '"':
                    in_string = False
        else:
            if ch == '"':
                in_string = True
                verbatim = pos > 0 and text[pos - 1] == '@'
            elif ch == '{':
                depth += 1
            elif ch == '}':
                depth -= 1
                if depth == 0:
                    end = pos + 1
                    while end < len(text) and text[end] in '\r\n':
                        end += 1
                    file.write_text(text[:start] + text[end:])
                    return
        pos += 1
    raise RuntimeError(f"{path}: unterminated body: {name}")


remove = {
    "test/SharpLink.UnitTests/Server/AdmissionDynamicPartitionRateTransitionTests.cs": [
        "PartitionSlidingWindowShapeUpdateShouldRetainHistoricalConsumption",
        "PartitionTokenBucketCadenceChangesShouldNotReplenishAtPublication",
    ],
    "test/SharpLink.UnitTests/Server/AdmissionDynamicRateLineageAndLifecycleTests.cs": [
        "RetainedOldRateLeaseAcrossDownstreamConcurrencyShouldNotBeChargedTwiceAfterUpdate",
        "LosingRateUpdateMustNotMutateSourceOrWinningTargetQuota",
    ],
    "test/SharpLink.UnitTests/Server/AdmissionDynamicRateReplacementRegressionTests.cs": [
        "LegacyAlgorithmReplacementShouldRetainSourceDebtUntilItsConservativeExpiry",
    ],
    "test/SharpLink.UnitTests/Server/AdmissionDynamicRateReviewRegressionTests.cs": [
        "DisableEnableMustKeepCurrentRateLineageWhileHistoricalWaiterCanLateGrant",
        "MultipleLegacyTokenWaitersMustNotCollapseAccumulatedTargetDebtToOnePeriod",
    ],
    "test/SharpLink.UnitTests/Server/AdmissionDynamicRateTransitionCarryRegressionTests.cs": [
        "TokenBucketUpdateShouldPreserveCadenceThatElapsedWhileBucketWasFull",
        "SlidingWindowLimitOnlyUpdateShouldPreserveIndividualSegmentAging",
    ],
    "test/SharpLink.UnitTests/Server/AdmissionDynamicRateSemanticsTests.cs": [
        "TokenBucketShrinkAndCadenceChangesShouldNotReplenishAtPublication",
        "SlidingWindowShapeUpdatesAtSegmentBoundaryShouldRetainHistory",
    ],
    "test/SharpLink.UnitTests/Server/AdmissionDynamicRateUpdateTests.cs": [
        "TokenBucketLimitUpdateShouldPreserveConsumedQuota",
        "SlidingWindowShapeUpdateShouldKeepHistoryThatRemainsInsideTheNewHorizon",
    ],
    "test/SharpLink.UnitTests/Server/AdmissionDynamicPartitionUpdateAdvancedTests.cs": [
        "LegacyPartitionAlgorithmReplacementShouldCarryRecentConsumption",
    ],
    "test/SharpLink.UnitTests/Server/AdmissionDynamicPartitionUpdateTests.cs": [
        "PartitionTokenBucketIncreaseShouldExposeOnlyDeltaQuota",
    ],
    "test/SharpLink.UnitTests/Server/AdmissionDynamicUpdateTests.cs": [
        "RateTransitionsShouldSucceedWhilePartitionTransitionsRemainTransactional",
    ],
}
for path, methods in remove.items():
    for method in methods:
        remove_test_method(path, method)

# Preserve the Fixed half of mixed tests while dropping the legacy Sliding/Token expectation.
path = ROOT / "test/SharpLink.UnitTests/Server/AdmissionDynamicRateTransitionCarryRegressionTests.cs"
text = path.read_text()
line = "    [Arguments(CarriedBarrierTarget.TokenBucket)]\n"
if text.count(line) != 1:
    raise RuntimeError("TokenBucket mixed carried-barrier argument mismatch")
path.write_text(text.replace(line, "", 1))

path = ROOT / "test/SharpLink.UnitTests/Server/AdmissionDynamicPartitionUpdateAdvancedTests.cs"
text = path.read_text()
line = "    [Arguments(PartitionRateKind.SlidingWindow)]\n"
# There are several Sliding arguments in the file. Remove only the one immediately before the delta-quota method.
marker = line + "    public async Task PartitionWindowRateIncreaseShouldExposeOnlyDeltaQuota"
if text.count(marker) != 1:
    raise RuntimeError("partition mixed sliding argument mismatch")
path.write_text(text.replace(marker, "    public async Task PartitionWindowRateIncreaseShouldExposeOnlyDeltaQuota", 1))

new_test = ROOT / "test/SharpLink.UnitTests/Server/AdmissionGenerationScopedRateTests.cs"
if new_test.exists():
    raise RuntimeError("AdmissionGenerationScopedRateTests.cs already exists")
new_test.write_text(r'''using System.Net;
using System.Threading;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionGenerationScopedRateTests
{
    [Test]
    [Arguments(GenerationRateKind.TokenBucket)]
    [Arguments(GenerationRateKind.SlidingWindow)]
    public async Task ChangedDefinitionStartsFreshGenerationWhilePinnedSourceRemainsIndependent(
        GenerationRateKind kind)
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options => ConfigureRate(options, kind, limit: 2, shape: 40));
        Ensure(source.TryAcquireUse(), "test must pin the source generation");
        try
        {
            await ConsumeAsync(source, 2);
            var sourceState = source.Controller.GlobalRateStateForTests!;

            var replacement = CommitUpdate(
                kernel,
                source,
                options => ConfigureRate(options, kind, limit: 3, shape: 60));
            var targetState = replacement.Controller.GlobalRateStateForTests!;
            Ensure(!ReferenceEquals(sourceState, targetState),
                $"{kind}: changed definition must create a fresh rate generation");

            await ConsumeAsync(replacement, 3);
            await EnsureRateRejectedAsync(replacement,
                $"{kind}: fresh generation must enforce only its own target capacity");
            await EnsureRateRejectedAsync(source,
                $"{kind}: pinned source must remain independently exhausted");
        }
        finally
        {
            source.ReleaseUse();
        }
    }

    [Test]
    [Arguments(GenerationRateKind.TokenBucket)]
    [Arguments(GenerationRateKind.SlidingWindow)]
    public async Task ExactDefinitionUpdateReusesStateAndCannotMintQuota(GenerationRateKind kind)
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options => ConfigureRate(options, kind, limit: 1, shape: 40));
        await ConsumeAsync(source, 1);
        var sourceState = source.Controller.GlobalRateStateForTests!;

        var replacement = CommitUpdate(
            kernel,
            source,
            options => ConfigureRate(options, kind, limit: 1, shape: 40));
        Ensure(ReferenceEquals(sourceState, replacement.Controller.GlobalRateStateForTests),
            $"{kind}: an exact definition update must reuse the same state");
        await EnsureRateRejectedAsync(replacement,
            $"{kind}: exact-definition publication must not expose fresh quota");
    }

    [Test]
    [Arguments(GenerationRateKind.TokenBucket)]
    [Arguments(GenerationRateKind.SlidingWindow)]
    public async Task OldQueuedWaiterDrainsOnSourceWithoutChargingFreshGeneration(GenerationRateKind kind)
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options =>
        {
            ConfigureQueue(options);
            ConfigureRate(options, kind, limit: 1, shape: 40);
        });
        Ensure(source.TryAcquireUse(), "old queued generation must remain pinned");
        try
        {
            await ConsumeAsync(source, 1, allowQueue: true);
            var queued = source.Controller.AcquireAsync(
                Context(), 7, allowQueue: true, CancellationToken.None).AsTask();
            Ensure(kernel.QueuedCalls == 1 && source.Controller.GlobalRateStateForTests!.WaitingCount == 1,
                $"{kind}: source waiter must be resident before update");

            var replacement = CommitUpdate(kernel, source, options =>
            {
                ConfigureQueue(options);
                ConfigureRate(options, kind, limit: 2, shape: 100);
            });
            await ConsumeAsync(replacement, 2);
            await EnsureRateRejectedAsync(replacement,
                $"{kind}: fresh target must be exhausted independently before old waiter wakes");

            time.Advance(TimeSpan.FromSeconds(40));
            var old = await queued;
            Ensure(old.IsAcquired, $"{kind}: old waiter must drain on its captured source generation");
            old.Lease!.Dispose();
            Ensure(kernel.QueuedCalls == 0,
                $"{kind}: old waiter completion must release outer queue accounting exactly once");
            await EnsureRateRejectedAsync(replacement,
                $"{kind}: late source grant must not be forwarded into the fresh generation");
        }
        finally
        {
            source.ReleaseUse();
        }
    }

    [Test]
    [Arguments(GenerationRateKind.TokenBucket, GenerationRateKind.SlidingWindow)]
    [Arguments(GenerationRateKind.SlidingWindow, GenerationRateKind.TokenBucket)]
    public async Task AlgorithmReplacementStartsFreshGeneration(
        GenerationRateKind sourceKind,
        GenerationRateKind targetKind)
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options => ConfigureRate(options, sourceKind, limit: 2, shape: 40));
        await ConsumeAsync(source, 2);

        var replacement = CommitUpdate(
            kernel,
            source,
            options => ConfigureRate(options, targetKind, limit: 2, shape: 60));
        await ConsumeAsync(replacement, 2);
        await EnsureRateRejectedAsync(replacement,
            $"{sourceKind}->{targetKind}: target generation must enforce its own two-permit budget");
    }

    [Test]
    [Arguments(GenerationRateKind.TokenBucket)]
    [Arguments(GenerationRateKind.SlidingWindow)]
    public async Task LosingChangedDefinitionCandidateCannotMutateLiveSource(GenerationRateKind kind)
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => ConfigureRate(options, kind, limit: 1, shape: 100));
        await ConsumeAsync(Current(server), 1);
        var source = Current(server);
        var sourceState = source.Controller.GlobalRateStateForTests!;

        SharpLinkServer.AfterAdmissionCandidateBuiltForTests = static (_, _) =>
            throw new InvalidOperationException("generation candidate fault");
        try
        {
            try
            {
                publicServer.UpdateAdmissionControl(options =>
                    ConfigureRate(options, kind, limit: 5, shape: 60));
                throw new Exception("assert failed: injected candidate fault did not escape");
            }
            catch (InvalidOperationException exception) when (exception.Message == "generation candidate fault")
            {
            }
        }
        finally
        {
            SharpLinkServer.AfterAdmissionCandidateBuiltForTests = null;
        }

        Ensure(ReferenceEquals(source, Current(server)) &&
               ReferenceEquals(sourceState, Current(server).Controller.GlobalRateStateForTests),
            $"{kind}: losing candidate must not replace or mutate the live source state");
        await EnsureRateRejectedAsync(Current(server),
            $"{kind}: source quota must remain exhausted after losing candidate");

        publicServer.UpdateAdmissionControl(options => ConfigureRate(options, kind, limit: 2, shape: 60));
        await ConsumeAsync(Current(server), 2);
        await EnsureRateRejectedAsync(Current(server),
            $"{kind}: winning changed definition must receive exactly its fresh capacity");
    }

    [Test]
    [Arguments(GenerationRateKind.TokenBucket)]
    [Arguments(GenerationRateKind.SlidingWindow)]
    public async Task PartitionChangedDefinitionStartsFreshGenerationPerExistingKey(GenerationRateKind kind)
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => ConfigurePartitionRate(options, kind, limit: 1, shape: 100));
        await ConsumeAsync(Current(server), 1);

        publicServer.UpdateAdmissionControl(options => ConfigurePartitionRate(options, kind, limit: 2, shape: 60));
        await ConsumeAsync(Current(server), 2);
        await EnsureRateRejectedAsync(Current(server),
            $"partition {kind}: existing key must receive one fresh two-permit target generation");
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

    private static AdmissionProgram CommitUpdate(
        AdmissionStateKernel kernel,
        AdmissionProgram source,
        Action<SharpLinkAdmissionControlOptions> configure)
    {
        var options = new SharpLinkAdmissionControlOptions();
        configure(options);
        options.Validate();
        var replacement = kernel.CreateUpdateProgram(source, options, [], out var plan);
        plan.Commit();
        source.Retire();
        return replacement;
    }

    private static SharpLinkServer CreateServer()
        => (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .Build();

    private static AdmissionProgram Current(SharpLinkServer server)
        => server.CurrentAdmissionProgramForTests ??
           throw new Exception("assert failed: expected enabled admission publication");

    private static void ConfigureRate(
        SharpLinkAdmissionControlOptions options,
        GenerationRateKind kind,
        int limit,
        int shape)
    {
        if (kind == GenerationRateKind.TokenBucket)
        {
            options.Global.UseTokenBucket(rate =>
            {
                rate.TokenLimit = limit;
                rate.TokensPerPeriod = limit;
                rate.ReplenishmentPeriod = TimeSpan.FromSeconds(shape);
            });
            return;
        }

        options.Global.UseSlidingWindow(rate =>
        {
            rate.PermitLimit = limit;
            rate.Window = TimeSpan.FromSeconds(shape);
            rate.SegmentsPerWindow = 4;
        });
    }

    private static void ConfigurePartitionRate(
        SharpLinkAdmissionControlOptions options,
        GenerationRateKind kind,
        int limit,
        int shape)
        => options.UsePartition(static _ => "tenant-a", partition =>
        {
            partition.MaxPartitions = 4;
            partition.IdleTimeout = TimeSpan.FromHours(1);
            if (kind == GenerationRateKind.TokenBucket)
            {
                partition.UseTokenBucket(rate =>
                {
                    rate.TokenLimit = limit;
                    rate.TokensPerPeriod = limit;
                    rate.ReplenishmentPeriod = TimeSpan.FromSeconds(shape);
                });
            }
            else
            {
                partition.UseSlidingWindow(rate =>
                {
                    rate.PermitLimit = limit;
                    rate.Window = TimeSpan.FromSeconds(shape);
                    rate.SegmentsPerWindow = 4;
                });
            }
        });

    private static void ConfigureQueue(SharpLinkAdmissionControlOptions options)
    {
        options.MaxQueuedCalls = 4;
        options.MaxQueuedBytes = 4096;
        options.MaxQueueDelay = TimeSpan.FromMinutes(2);
    }

    private static async Task ConsumeAsync(
        AdmissionProgram program,
        int count,
        bool allowQueue = false)
    {
        for (var index = 0; index < count; index++)
        {
            var decision = await program.Controller.AcquireAsync(
                Context(), 1, allowQueue, CancellationToken.None);
            Ensure(decision.IsAcquired, $"expected permit {index + 1} of {count}");
            decision.Lease!.Dispose();
        }
    }

    private static async Task EnsureRateRejectedAsync(AdmissionProgram program, string scenario)
    {
        var decision = await program.Controller.AcquireAsync(
            Context(), 1, allowQueue: false, CancellationToken.None);
        decision.Lease?.Dispose();
        Ensure(!decision.IsAcquired && decision.Reason == "rate", scenario);
    }

    private static SharpLinkAdmissionContext Context()
        => new(101, 202, RpcMethodKind.Unary, "generation-scoped-rate", null, null);

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }

    public enum GenerationRateKind
    {
        TokenBucket,
        SlidingWindow
    }
}
''')

print("issue #410 generation-scoped rate tests staged")
