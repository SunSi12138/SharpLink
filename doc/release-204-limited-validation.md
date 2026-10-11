# SharpLink 2.0.4: explicitly limited release performance acceptance

The release owner approved this release-specific reduction on 2026-10-11 after reviewing the full-matrix time breakdown. This exception applies only to 2.0.4. It does not claim a full five-repetition matrix, change later releases' default policy, or inherit the 2.0.3 sampler-interference exception.

## Exact scope

- Run the unchanged canonical `eng/run-performance-matrix.sh` with the full-tier defaults except these explicit partition/reduction variables: three repetitions; payloads 0, 32, 65536 and 1048576 bytes; concurrency 1 and 32. The fixed-queue OneWay workload remains concurrency 1 by canonical script design.
- Include TCP, UDS, NamedPipe, AnonymousPipe and SharedMemory; Balanced, LowLatency and Throughput; pools 1/1 and 1/4, except AnonymousPipe's canonical 1/1-only restriction.
- Include unary, bounded-retry fixed-queue OneWay, raw profile-default OneWay backpressure, asynchronous yield/delay and all four canonical streaming operations (unary, c2s, s2c, duplex).
- Preserve five-second warmup, twenty-second measurement, 300M sample capacity, and 64 MiB fixed queues wherever canonical code specifies them. The raw backpressure workload deliberately keeps its profile-default queue and no retry override.
- Partition by transport/profile/workload into 75 standard `ubuntu-latest` jobs, maximum ten simultaneous matrix jobs. Preserve three independent repetitions inside each shard. Record every host separately; different hosted runners are not a controlled cross-host performance comparison.
- Expected completeness: 729 raw matrix JSON reports and 1,863 measured stages. Reject missing, duplicated, unexpected, wrong-SHA, wrong-setting, truncated or invalid evidence. Every measured failure blocks acceptance, including raw backpressure failures. The frozen recorder groups all `ResourceExhausted` causes together and records only the first detail, so its current evidence cannot prove every rejection was intentional send-queue saturation. This is an insufficient-evidence blocker, not by itself proof of a production correctness bug. A log example is not a waiver. Keep the unchanged raw workload and all logs; do not retry with different settings, suppress errors, or label this as zero-error throughput. If this gate fails, resolve the evidence/source limitation through a reviewed new candidate and rerun exact-final-source gates.

Omitted from this matrix: two of the normal five repetitions; payloads 256 and 4096 bytes; concurrencies 8, 128, 256 and 512. The unchanged recorder baseline separately includes c128/c512 controls and additional feature/stream representatives, but does not restore the omitted full-matrix cells. No version-to-version performance improvement is inferred from these acceptance samples.

## Requirements unchanged

Run the original `eng/run-latency-recorder-baseline.sh` without reduced settings or changed analyzer/thresholds. All micro 1M × 5, alternating interference, Dual accuracy, feature representatives and report checks must pass. P99.9 failures remain blocking for 2.0.4; the 2.0.3 exception is not reused. Gate this before the costly matrix launch.

Run the canonical TCP and SharedMemory ten-minute release soaks in parallel with unchanged concurrency, restart, checkpoint, fault-injection, recovery and leak checks. Run Generated ABI Dynamic-module Churn for at least 600 seconds, completing the last round and recording actual duration. This is not two-hour/five-hour stability evidence.

The canonical release-soak duration timer starts before setup/warmup, while `ActualElapsedSeconds` is measured from a later post-warmup timestamp. Acceptance therefore requires configured duration 600 seconds, a final successful report, and at least 600 seconds between `StartedUtc` and `TimestampUtc`. Preserve the finite positive `ActualElapsedSeconds` separately; it can legitimately be below 600 seconds and is not represented as 600 measured seconds. The aggregate records both intervals, all six operation attempts and the same five drained resource gauges tracked by the source.

All correctness tests, security scans, eight-package identity/dependency/symbol checks, empty-cache installation, AOT, three-platform Release Gate, branch protection and release-environment requirements remain unchanged. Package publication and tags remain blocked until every required gate passes.

An initial tooling job runs the synthetic regression suite and a real .NET 10 positive/negative DLL-metadata smoke before any long validation. Source Unit tests use the Release Gate's `TUNIT_MAX_PARALLEL_TESTS=8` setting without changing concurrency inside tests. A concurrent exact-source dependency job restores normally, then runs separate `dotnet package list --project Sharplink.slnx --no-restore --include-transitive --vulnerable/--deprecated --format json --output-version 1` scans. It preserves findings and diagnostics and conservatively blocks on any vulnerability or deprecation finding. It does not edit dependency versions or configuration.

## Immutable provenance and sequence

Validation tooling may live on a separate reviewed validation commit. Every source job separately checks out the requested 40-character production SHA, verifies its clean tree, and records OS/CPU/.NET, exact tooling/source/run/attempt identities, main identity and canonical script hashes. Build/test jobs inspect assembly versions and informational versions directly from the same actual DLL bytes they hash, without executing those assemblies. Required tested entrypoints and dependencies must be present and identify `2.0.4+<exact source SHA>`. Generated source metadata and environment labels alone are insufficient. Recheck the clean tree afterward. Outputs live outside both checkouts. The aggregate compares script hashes against its own clean checkout of the exact source and rejects mixed run attempts.

The `final_main=true` path also requires the production SHA to equal the fetched main tip. Candidate dry runs (`final_main=false`) are explicitly not final release evidence. After merging the release PR, rerun Release Gate and this validation workflow against the exact final main SHA that will be tagged. Do not reuse PR-head results or mix artifacts from different commits/runs. The aggregate downloads only its own run's artifacts. A failed or incomplete gate is not a waiver or a retry authorization for changed settings.

Pushes to the dedicated `validation/2.0.4-release-evidence` branch run this workflow against immutable final-main commit `85f5f87c21056edc16af80d6c9c06cfca83d8cfa` with `final_main=true`. Manual inputs remain available when GitHub exposes the workflow. A new push is an explicit new run; there is no automatic retry loop. Recheck remote main immediately before tagging.

The aggregate records `MatrixPassed`, every independent job conclusion, `LimitedValidationPassed`, and `FinalMainLimitedValidationPassed`. These fields cannot replace one another. `ReleaseEligible` remains false in this performance-validation artifact because separate Release Gate, branch protection, release-environment and publication requirements are not attested by this workflow. The release coordinator must verify those independent requirements on the same exact source before publication; a green matrix or candidate-only run cannot authorize a release.

## Time budget

The full five-repeat matrix is 1,485 reports/9,990 stages: unary 29.06, fixed OneWay 0.94, raw backpressure 5.63, async 11.25 and streams 22.50 nominal runner-hours (69.375 total before startup/drain/build).

This approved limited matrix is 12.94 nominal runner-hours and approximately 16–20 runner-hours including overhead; about 2–3 hours elapsed at ten-way concurrency. The unchanged recorder baseline adds approximately 60–90 minutes before matrix jobs begin, for approximately 3–4.5 hours end-to-end if all gates pass and runners are available. The two release soaks and ABI churn run independently and are not the dominant cost. These are planning estimates, not shortened timeout or success criteria.

Only standard public-repository GitHub-hosted compute is used. No paid larger runners, secrets, publishing credentials or expanded repository permissions are needed. Artifact storage is separate from compute billing.
