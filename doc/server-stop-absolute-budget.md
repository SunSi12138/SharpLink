# Preserve the server Stop observer's absolute budget

## Incident and scope

The [lease regression run](https://github.com/SunSi12138/SharpLink/actions/runs/39761275956) for public head
`f11866a00505747126e28cf4e292fd6d47a055a6` failed only
`StopBudgetMustNotDisposeRuntimeBeforeActiveOrRetiredCallback(True)` in its third
full pass. The first two passes were 2,402/2,402. The failure was the primary
five-second `Join(stop)`, not the final cleanup join. Releasing the held callback
in `finally` allowed cleanup to finish. The exact hosted timer-sampling
interleaving was not captured, so the source-negative schedules below establish a
real defect compatible with that failure, not proof of its unique hosted cause.

The public reproduction base is `f11866a00505747126e28cf4e292fd6d47a055a6`, whose production
source tree is `8915e61e5534f6e47a5d679bfbea5c0dcb812eaa`. Its complete Git tree
`02eb8677d924c388d3ca840d503a9a80d531f0bf` matches the locally tested reproduction
checkout. Its relevant
`WaitUntilWithProviderAsync` method is byte-identical to the method at
`13928a35df4bff3cd99b14bd1df131aa3eb3bcdb`. This is an existing cold shutdown helper
defect, not a demonstrated producer-reservation regression or ThreadPool leak.

## Deterministic source negatives

Stop already gives successive framework/service cleanup waits one absolute
`finalDeadline`. The helper instead converted that deadline to a relative duration,
then called a helper that sampled a fresh origin and registered a relative timer.
An earlier phase's timer can satisfy the fixture's earliest-timer observation
without proving the later phase has finished this conversion.

The new tests do not use load or sleeps to choose the following schedules:

| Schedule | Original absolute deadline | Clock after injected movement | Old timer due | Baseline |
| --- | ---: | ---: | ---: | --- |
| Advance between the remaining-duration sample and the nested origin sample | 5 s | 5 s | 10 s | Intended failure |
| Advance inside `CreateTimer`, before installing its relative due time | 5 s | 5 s | 10 s | Intended failure |
| Stationary clock | 5 s | 0 s when armed | 5 s | Pass |

The final authored fixture on unchanged production has **16 passing controls and
2 intended failures**. The same 18 cases pass with the correction. The configured
five-second cleanup budget and every existing five-second fixture watchdog remain
unchanged. No future timer is indiscriminately advanced to make Stop complete.

## Correction and terminal precedence

Only `ServerLifecycleCoordinator.Cleanup.cs` changes in production. After its
unchanged initial completed-source and expired-snapshot checks, it passes
`RpcDeadline.FromTimestamp(deadline)` to the existing absolute timer path. That
path owns a disarmed timer before sampling/arming and rechecks expiry after timer
registration. `FromTimestamp` preserves the existing absolute boundary, including
large graceful deadlines, without applying finite RPC-budget normalization.

The RPC helper normally gives deadline expiry priority. The Stop adapter therefore
rechecks a completed source after a false deadline result and awaits that source.
Tests preserve the original exception instance and cancellation token, not just a
boolean result:

- An already-completed success, fault or cancellation wins at entry, even after
  expiry, without creating a timer.
- Completion during timer creation or immediately before the deadline callback
  retains its success/fault/cancellation outcome.
- A source completed during the initial already-expired timestamp sample still
  loses to that initial expiry check, as before.

These are explicit ordering controls, not a claim that every possible historical
micro-interleaving is equivalent. Shared timer code, ordinary RPC execution,
callback ownership, deferred service cleanup and the original failing fixture are
unchanged. Timeout remains an observer result, never permission to reclaim an
unfinished physical owner. The correction adds no ordinary RPC-path work; it does
reuse the existing allocating absolute timer signal during cold shutdown waits.

## Validation and reproduction

Release build: zero warnings/errors. The selected official .NET 10.0.13 runs use
`DOTNET_PROCESSOR_COUNT=1` and `TUNIT_MAX_PARALLEL_TESTS=8`:

| Group | Passed |
| --- | ---: |
| New absolute Stop controls | 18/18 |
| Existing callback ownership/lifecycle/transfer/queued-shutdown controls | 19/19 |
| Existing hosted-service/coordinator/lifecycle-ownership controls | 18/18 |
| Total selected cases | 55/55 |

The hosted-service group includes the timer-range-exceeding graceful wait.
Maintainability, production project-reference boundaries and `git diff --check`
pass. Initial exploratory assertions inspected timer retirement before the .NET
waiter's cleanup tail finished; the final fixture joins the existing timer-drained
signal before asserting zero timers. Those exploratory failures remain retained,
as do one invalid union-filter invocation and one guard invocation lacking the
SDK environment. None is reported as a passing test execution.

Portable commands from a configured SDK checkout:

```sh
dotnet build test/SharpLink.UnitTests/SharpLink.UnitTests.csproj -c Release \
  -m:1 -nr:false -p:UseSharedCompilation=false
DOTNET_PROCESSOR_COUNT=1 TUNIT_MAX_PARALLEL_TESTS=8 dotnet \
  test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll \
  --treenode-filter '/*/*/ServerStopAbsoluteBudgetRebaseTests/*'
```

To reproduce the negatives, create a worktree at the base above and copy only
`test/SharpLink.UnitTests/Server/ServerStopAbsoluteBudgetRebaseTests.cs` into it.
Build and use the same command. No production compatibility adapter is needed.

Authored source SHA-256 values:

- `ServerLifecycleCoordinator.Cleanup.cs`:
  `765fdd303dab0c10c3750d7481af9908570cf5af6ab84e836c46bbb1e35ebcba`
- `ServerStopAbsoluteBudgetRebaseTests.cs`:
  `8ae88806d3117a1c83d4780b0a39fbdc3199e293bd868b7363035f0a0ce3d278`

Fresh integrated CI and source-bound cost-kit rebinding remain delivery gates;
focused local controls alone do not make the previous red head accepted.
