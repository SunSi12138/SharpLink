# 0.8.19 regression-test research

## Target inventory and evidence candidates

- `SharpLinkAuthenticationResult` has a public positional constructor, but the Server accepts any result whose `IsAuthenticated` bit is true. A custom provider can therefore return an authenticated result carrying a rejection code and bypass the authentication failure path.
- Client and Server interceptor pipelines share one mutable continuation index. Calling `next` twice advances beyond the configured chain and invokes a non-idempotent terminal RPC or service method a second time; concurrent duplicate calls can also bypass an interceptor stage.
- Client background-task tracking removes completed faulted tasks without observing or logging their exception. A cleanup task that faults before Stop snapshots the set becomes invisible to both Stop and diagnostics.
- Generic Host Server Stop performs Server disposal in a `finally` without preserving a prior caller-cancellation or Stop failure. A later listener cleanup failure can replace the first failure instead of retaining both causes.
- Public endpoint polling and heartbeat intervals can exceed the native timer range and fail immediately instead of waiting; Server admission accepts the same oversized queue delay and defers the failure until a call is already queued.

## Acceptance checklist

- Authenticated provider results are accepted only with the success sentinel error code; malformed success is converted to a structured authentication rejection.
- Every interceptor receives a single-use continuation on Client and Server, and duplicate `next` calls cannot execute or bypass the terminal operation.
- Every faulted tracked Client background task is observed and logged after removal; cancellation remains non-error completion.
- Hosted Server Stop completes all owner cleanup and preserves primary and disposal failures in order.
- Long endpoint polling and heartbeat intervals wait in cancellable timer slices, while an admission queue delay beyond the portable timer range fails during configuration.

## Audit guardrails

The batch is limited to independently reproducible security, side-effect duplication, timer, ownership, and failure-observability defects. A topology-lifecycle callback candidate was discarded because its interface is internal and its only production implementation cannot throw in the proposed way. Public constructor removal, interceptor fast-path changes when no interceptor is configured, and unrelated cold-path syntax cleanup are excluded unless evidence shows material value.

## Regression and performance evidence

- The complete pre-fix Integration run retained 228 existing passes and failed the two new authentication/interceptor tests. Unit probes independently observed the missing background log and lost Hosted cancellation cause; the timer-stage full run retained 434 passes and failed exactly the two new timer tests.
- Post-fix Unit passes 436/436 and Integration passes 230/230.
- Counterbalanced, tiering-disabled TCP unary A/B retained about 320.01 B/op without interceptors and overlapping latency ranges. One Client plus one Server interceptor changes from 1552.01 B/op to 1584.01 B/op; the deliberate 32 B is two per-stage single-use guards, while medians remain in the same roughly 40–41 microsecond band.
