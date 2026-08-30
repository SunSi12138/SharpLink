# CI validation tiers baseline

This document is the decision record for #374 and the input contract for #375 and #376.

Snapshot: `dev`, 2026-08-30.

This is an analysis/documentation change only. It does not migrate, remove, rename, or change the trigger/blocking behavior of any workflow.

## Measurement method

The baseline uses successful `PR Quick` runs from representative pull requests targeting the current development line. Active elapsed time is measured from the workflow run start to completion; queue time before the active attempt is excluded. Step durations are rounded to whole seconds from GitHub Actions job timestamps.

Representative successful runs:

| PR | Change shape | PR Quick run | Attempt | Active elapsed |
| --- | --- | --- | ---: | ---: |
| #388 | Small dependency update | [#2303](https://github.com/SunSi12138/SharpLink/actions/runs/33045944173) | 1 | 6m 47s |
| #392 | Focused deterministic test fix | [#2412](https://github.com/SunSi12138/SharpLink/actions/runs/33243138101) | 1 | 7m 03s |
| #386 | Large feature branch | [#2484](https://github.com/SunSi12138/SharpLink/actions/runs/33289641055) | 3 | 7m 10s |

The representative range is **6m 47s to 7m 10s**, with a **7m 03s median** active wall-clock duration.

`PR Quick` on `dev` currently consists of the serial `quick` job plus the reusable codec-compatibility workflow. The sampled #2484 run expanded to **23 jobs** in total: one `quick` job and 22 codec-compatibility jobs.

## Current `quick` job cost

Run [#2484](https://github.com/SunSi12138/SharpLink/actions/runs/33289641055) is used as the detailed step sample. Its `quick` job ran for about **7m 05s**.

| Current step | Approx. duration |
| --- | ---: |
| Checkout | 1s |
| Setup .NET | 1s |
| Restore | 10s |
| Verify Formatting | 23s |
| Build Debug | 49s |
| Build Release | 39s |
| Verify Generated Assemblies Do Not Reference Runtime | 1s |
| Unit Tests | 17s |
| Generator Tests | 31s |
| Load Test Tests | 2s |
| Integration Tests | 40s |
| Run NativeAOT Transport and Topology Smoke | 53s |
| Pack | 2s |
| Verify SDK Contains Generator | <1s |
| Verify package metadata, XML documentation, and symbols | <1s |
| Verify Hosting direct Runtime dependency | 3s |
| Verify Abstractions has no DI dependency | 3s |
| Restore Package Smoke | 2s |
| Run Package Smoke | 5s |
| Demo Oneway | 1s |
| Load Smoke | 15s |
| Chaos Smoke | 121s |
| Upload Chaos Report | 1s |

The seven largest contributors are Chaos Smoke (121s), NativeAOT smoke (53s), Debug build (49s), Integration Tests (40s), Release build (39s), Generator Tests (31s), and formatting (23s). Together they account for roughly **84%** of the sampled `quick` job.

The most important observation is that the current blocking-shaped path mixes bounded compile/test feedback with environment-sensitive, packaging, AOT, load, and intentionally time-based chaos validation.

## Codec compatibility cost shape

The current reusable codec workflow adds broad cross-platform confidence but is not a fast-gate-shaped check. It expands into:

- 6 desktop producer jobs: Linux x64/arm64, Windows x64/arm64, macOS x64/arm64.
- 1 Browser/WASM producer job.
- 6 desktop cross-verification jobs.
- 1 Browser/WASM verification job.
- 6 Browser-to-desktop verification jobs.
- 1 Browser evidence aggregation job.
- 1 desktop compatibility summary job.

That is **22 jobs** per invocation before counting the parent `quick` job.

As a representative cost shape from run #2412, Browser/WASM production took about **1m 24s**; installing `wasm-tools` accounted for about 19s and publishing the browser probe for about 57s. The desktop summary job took about **29s**, including about 21s of aggregation. Other jobs run in parallel, so their main impact is runner consumption, platform/environment variance, and fan-out failure surface rather than simply adding their durations to the serial `quick` job.

## Tier definitions

The tiers are defined by feedback purpose rather than by where a check happens to live today.

| Tier | Purpose | Signal | Cost / variance | Blocking intent |
| --- | --- | --- | --- | --- |
| **Fast** | Catch common correctness and source-quality regressions before merge | High and immediate | Strictly bounded; low environmental variance | Blocking for normal PRs to `dev` once #375 implements and wires the status |
| **Extended** | Validate integration, packaging, AOT and compatibility confidence that is valuable on PRs but too costly for the fast feedback loop | High, often release-relevant | Medium/high or platform-sensitive | Visible on PRs; advisory by default unless a later policy explicitly makes it required |
| **Nightly / merge-to-dev** | Exercise stochastic, endurance, broad matrix and expensive confidence checks after merge and on schedule | High confidence, lower immediacy | High, intentionally time-based, or higher flake/environment exposure | Non-blocking for the normal PR fast gate |
| **Release** | Preserve the existing comprehensive release contract for `main` and tags | Release-critical | High, comprehensive | Existing `release-summary` remains required on `main` |

`merge-to-dev` means a post-merge `push` to `dev`; it is not a second pre-merge blocker.

## Intended ownership of current `quick` validation

Setup/checkout/upload plumbing follows the tier that owns the validation it supports and is not independently classified as product signal.

| Current validation | Signal | Cost | Flake / environment exposure | Release criticality | Intended tier | Rationale |
| --- | --- | --- | --- | --- | --- | --- |
| Restore | High | Low | Low | High | **Fast** | Required prerequisite and catches dependency/project graph breakage early. |
| Verify Formatting | High | Low/medium | Low | Low | **Fast** | Deterministic source-quality failure with immediate author action. |
| Build Debug | Medium | Medium | Low | Medium | **Extended** | A second full configuration build duplicates most compile cost; useful configuration coverage but not necessary for fastest feedback. |
| Build Release | High | Medium | Low | High | **Fast** | Production configuration and prerequisite for the bounded tests/guards. |
| Verify Generated Assemblies Do Not Reference Runtime | High | Low | Low | High | **Fast** | Very cheap architecture boundary guard with high regression value. |
| Unit Tests | High | Low/medium | Low | High | **Fast** | Primary bounded regression signal. |
| Generator Tests | High | Medium | Low | High | **Fast** | Source-generation correctness is core compile/runtime behavior and remains bounded. |
| Load Test Tests | High | Low | Low | Medium | **Fast** | These are the load-test component's unit tests, not a timed load run; sampled cost is about 2s. |
| Integration Tests | High | Medium | Medium | High | **Extended** | Important end-to-end signal but materially lengthens the serial gate and has more scheduling/environment exposure. |
| NativeAOT Transport and Topology Smoke | High | High | Medium/high | High | **Extended** | Native publish/toolchain coverage is release-relevant and expensive; nightly/release retain broader platform coverage. |
| Pack | High | Low after build | Low | High | **Extended** | Package production is the prerequisite for package-specific validation, not the normal source fast gate. |
| Verify SDK Contains Generator | High | Low | Low | High | **Extended** | Package-content contract; keep next to Pack. |
| Verify package metadata, XML documentation, and symbols | High | Low | Low | High | **Extended** | Package artifact contract; keep next to Pack. |
| Verify Hosting direct Runtime dependency | High | Low | Low/medium | High | **Extended** | Package dependency contract and restore-based package smoke; semantically belongs with package validation. |
| Verify Abstractions has no DI dependency | High | Low | Low/medium | High | **Extended** | Package dependency contract; semantically belongs with package validation. |
| Restore Package Smoke | High | Low | Medium | High | **Extended** | Exercises locally produced packages and NuGet restore behavior. |
| Run Package Smoke | High | Low | Medium | High | **Extended** | Consumer-style package validation, valuable but outside the compile/unit fast loop. |
| Demo Oneway | Medium | Low | Medium | Low/medium | **Extended** | Lightweight executable smoke; useful integration confidence, not a unique fast-gate invariant. |
| Load Smoke | Medium/high | Medium | Medium/high | Medium | **Extended** | Timed performance/load smoke depends more on runner conditions than unit-style checks. |
| Chaos Smoke | High confidence, low immediacy | High / fixed 120s | High | High | **Nightly / merge-to-dev** | Stochastic, restart-oriented and intentionally time-based; it currently consumes almost two minutes by construction. |
| Upload Chaos Report | Diagnostic | Low | Low | N/A | **Nightly / merge-to-dev** | Follows the owning Chaos validation. |

## Intended ownership of codec compatibility validation

| Current codec validation | Intended tier | Rationale |
| --- | --- | --- |
| Desktop `produce` matrix (6 platforms) | **Extended** | Cross-platform wire evidence is release-relevant but expensive and fan-out-heavy. |
| Desktop `verify` matrix (6 platforms) | **Extended** | High-value compatibility signal that depends on producer artifacts and multiple runners. |
| Desktop `summary` | **Extended** | Aggregates the blocking desktop compatibility result and should follow the desktop matrix. |
| Browser/WASM `browser-produce` | **Nightly / merge-to-dev** | Requires WASM workload installation/publish and is already tolerant of environmental failure. |
| Browser/WASM `browser-verify` | **Nightly / merge-to-dev** | Broad platform evidence with higher toolchain/browser variance; currently `continue-on-error`. |
| Browser-to-desktop matrix (6 platforms) | **Nightly / merge-to-dev** | Six-runner reverse compatibility evidence with SDK pinning and high environmental surface; currently `continue-on-error`. |
| Browser evidence aggregation | **Nightly / merge-to-dev** | Follows Browser/WASM evidence and is currently `continue-on-error`. |

Artifact download/upload, checkout, SDK setup and SDK-version recording follow the validation tier above.

The release gate remains free to run the full compatibility workflow independently of these PR-tier assignments.

## Fast gate budget and contract for #375

The Fast tier should be implemented with a stable workflow/status contract and a hard time bound:

- Recommended workflow name: **`PR Fast`**.
- Recommended stable job/status context: **`fast`**.
- Trigger: every pull request targeting `dev` plus `workflow_dispatch` for diagnosis.
- Expected successful active duration: **<= 3 minutes** under normal hosted-runner conditions.
- Hard job timeout: **5 minutes**.
- Intended blocking semantics: required for normal merges to `dev` after the status exists and the repository ruleset is deliberately updated.

The current #2484 sample gives a practical basis for the <=3 minute target: restore + formatting + Release build + architecture guard + unit tests + generator tests + load-test unit tests consume about two minutes of observed step time before normal setup/cleanup overhead. #375 also expects the maintainability baseline check; that is a **new Fast check**, not a current `PR Quick` step, and it must fit inside the same budget.

If the implemented Fast workflow cannot normally stay inside the three-minute target, membership should be re-evaluated rather than increasing the timeout until the distinction from Extended disappears.

## Extended and Nightly trigger contract for #376

#376 should preserve every capability while moving it out of the normal Fast critical path.

| Tier | Recommended trigger | PR merge blocking | Notes |
| --- | --- | --- | --- |
| Fast | `pull_request` -> `dev`, manual | Yes, after ruleset wiring | Bounded source/build/test feedback. |
| Extended | `pull_request` -> `dev`, manual | Advisory by default | Run concurrently with Fast so expensive confidence does not delay the Fast result. Change-path filtering can be considered only if it does not silently drop required coverage. |
| Nightly / merge-to-dev | `push` -> `dev`, nightly schedule, manual | No | Owns stochastic/endurance/broad platform evidence and provides post-merge confidence. |
| Release | Existing PR -> `main`, tag, manual | Existing `release-summary` contract | Keep the current comprehensive release gate independent of PR tier refactoring. |

Workflow deduplication remains a separate concern under #348; #374 does not require consolidating reusable jobs.

## Status names and repository rulesets

Repository rulesets observed on 2026-08-30:

- **Protect dev integration** applies to `dev`. It currently has pull-request/merge rules but **no required status checks**.
- **Protect main release** applies to `main` and currently requires the **`release-summary`** status.

Implications:

1. This #374 documentation PR makes **no ruleset change**.
2. #375 can introduce `PR Fast` / `fast` without having to preserve a currently-required `PR Quick` context on `dev`, because no required check is configured there today.
3. If `fast` is to become required, create and successfully exercise the new status first, then add that exact context to **Protect dev integration**. Do not configure a required context before GitHub has produced it.
4. Extended should expose a stable visible result (for example an `extended-summary` aggregate if #376 chooses to add one), but it should remain advisory unless the merge policy is explicitly changed later.
5. Do not rename or remove `release-summary` as part of #375/#376. `main` currently relies on that exact required context.

## Acceptance checklist for #374

- [x] Current gate duration and major contributors are recorded.
- [x] Every current serial `quick` validation has an intended target tier and rationale.
- [x] Every codec-compatibility validation family has an intended target tier and rationale.
- [x] The Fast gate has a documented expected duration (<=3m) and hard timeout (5m).
- [x] Current status/ruleset implications for `dev` and `main` are documented.
- [x] No workflow migration is included.

Follow-up implementation order remains **#375 -> #376**.
