# Absolute Stop budget: source and validation status

## Contract and change

Sequential server shutdown and cleanup phases share one absolute Stop deadline.
Rebasing a relative timeout while arming a timer can extend that original budget.
`ServerLifecycleCoordinator.WaitUntilWithProviderAsync` now gives the existing
absolute timestamp to `SharpLinkTimer.WaitAsync` rather than starting a new
relative wait.

The helper keeps the existing priorities: a source already completed at entry
propagates its success, exception or cancellation; an initially expired deadline
returns timeout; after a timer race, an already terminal source retains its
original terminal result. This is a cold shutdown/terminal-cleanup correction,
not a new per-RPC admission, reservation or producer-lifetime mechanism. It does
not adopt the other work proposed in #933 or complete #775.

## Deterministic source controls

The 18-row `ServerStopAbsoluteBudgetRebaseTests` fixture covers timer-registration
clock advances, entry completion, deadline completion and initial-expiry priority.
The unchanged baseline helper is expected to fail only the `Timestamp` and
`CreateTimer` arming rows: its timer due timestamp becomes 100,000,000 ticks, beyond
the original 50,000,000-tick deadline. The fixed helper must pass all rows. The
existing `TimerRangeExceedingServerGracefulWaitShouldRemainPending` test is the
long-timeout control. No transport or performance workload is part of this proof.

## Evidence availability after workspace recovery

The base is actual dev `3fafced96ea9c1cfc5398b5d8d5a20bb4e39c1e9`.
The helper and identical test fixture were reconstructed from retained repository
objects, producing exactly tree `fd150717888544a7ac74452d3ee979bbafa66150`.
The six-file census maintenance also reproduced exactly tree
`d9cb8ef0495d6f8fa9191d18016d422bd3f11e6f`. This establishes source equality only.
The test blob is `99f2ccf180b1c9f7ac721c06e39178ccbfc1997e` (SHA-256
`8ae88806d3117a1c83d4780b0a39fbdc3199e293bd868b7363035f0a0ce3d278`).

Before the execution workspace was replaced, local validation was reported as:
19 fixed passes including the long-timeout control, 16 baseline passes plus the
two expected counterexamples, and six passing Python census/parser guard tests.
Both discoveries were reported as 2,217 tests, including five explicit probes.
The original result-classifier attempt failed because native TUnit reports these
`InvalidOperationException` nodes as `error`, not `failed`; a separate read-only
reconciliation was reported successful without repeating the tests.

**Those raw logs, binaries, packages and review artifacts are no longer available
in the recovered workspace.** The reports above are historical status, not fresh
acceptance or replacement artifacts. Source equality does not restore the missing
receipts. This documentation revision has not been built or tested. Fresh hosted
validation on the durable published source must retain exact commit, binary,
package, report and failed-attempt identities before release acceptance.

The ordinary count expectation is 2,212; the lease-race harness adds its unchanged
eight controls for 2,220. Neither number is a claim that a full suite ran. All exact
count, failure, skip and historical-marker assertions remain unchanged. No
performance regression, release or broad optimization is accepted by this change.
