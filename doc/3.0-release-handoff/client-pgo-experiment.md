# Client-only default-PGO profile: executable handoff

Candidate commit: `51f5bd0ec3f600196f2e26ea48a466ec2cea62d8`.
Base: `dd02ecfc6970cfa3b621d919d48e4d9e48f205aa`.
Branch: `codex/931-client-ablation-tools`.
Worktree: `/Users/sunsi/.codex/worktrees/physical-ownership-performance-tools/SharpLink`.
Independent review and root push/dispatch approval remain required.

## Current scientific decision

FullOpts run40642932620 completed 12 processes / 247595 RPCs. Parent independently
reported paired throughput +0.90%, CPU/op -0.57%, P99 -0.99%, P99.9 -0.50%, mixed
pair directions and intervals spanning 1. The original default-PGO full-product
NoGo remains. This follow-up isolates default tiering/PGO in the same legal
Client-only composition; it introduces no product candidate.

## Frozen default-PGO plan

Old Client cc7c458b559295e0fc61ec0062bb220faf880a76 vs new Client
ac17c8d8ea0203edbe7b8cf19ddb95eaa24d4d2c. Both arms use candidate Runtime, Server,
Abstractions and every other DLL. Dynamic intercepted TCP100, one worker and one
connection, two selected allowed logical CPUs with explicit topology, private
.NET10.0.13. Clear tiering/PGO overrides; do not force thresholds.

Each arm has one fixed 4352-RPC non-timed preflight, including the first traced
call. Pause exactly 250ms after completed calls 1024/2048/3072/4096, then finish
the bounded workload. Four real TCP closed-generic MoveNext methods must each
have positive-size Tier1 or Tier1-OSR native. Tier0 and instrumented records are
retained but not accepted. No adaptive polling, retry or additional warmup.

Only after both native gates pass: six alternating AB/BA pairs, 12 fresh measured
processes, each with 3s warmup and 6s measurement, native disassembly off. Failure
retention, identity/tick validation before summary, and complete-six-pair guard
remain intact. No additional performance threshold.

The preflight's Tier1 proof belongs to those preflight processes; measured
processes retain default adaptive tiering and the unchanged original warmup.

## Validation obtained

- 19 Python fixtures pass; actionlint and diff-check pass.
- Updated standalone probe built with SDK10.0.102, 0 warnings/errors.
- Actual local macOS arm64/.NET10.0.11 preflight: both arms completed exactly
  4352 RPCs, exit0, all four required real-path Tier1 targets captured.
- `local-verification.json`, both raw proof JSON, host outcomes, logs and native
  listings are beside this handoff. No timed process was executed locally.
- Local runtime/CPU arrangement is not hosted private10.0.13 acceptance. The
  formal gate must pass on its actual runner. No push or dispatch by this owner.

## Review and dispatch (root only after approval)

```sh
git diff dd02ecfc..51f5bd0e -- .github/workflows/performance-evidence.yml eng/physical-ownership-probes
git rev-parse HEAD
git push -u origin codex/931-client-ablation-tools
gh workflow run performance-evidence.yml --repo SunSi12138/SharpLink \
  --ref codex/931-client-ablation-tools \
  -f suite=ownership-931-client-ablation-pgo -f repetitions=1
gh run list --repo SunSi12138/SharpLink --workflow performance-evidence.yml \
  --branch codex/931-client-ablation-tools --event workflow_dispatch --limit 5 \
  --json databaseId,headSha,status,conclusion,url,createdAt
```

Record the newly created run ID with exact reviewed headSha. Do not redispatch
because the dispatch result is uncertain. Existing suite
`ownership-931-client-ablation` still selects FullOpts.

```sh
gh run view "$RUN_ID" --repo SunSi12138/SharpLink --json databaseId,headSha,status,conclusion,url,jobs
gh run download "$RUN_ID" --repo SunSi12138/SharpLink \
 --name physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1 \
 --dir "$DESTINATION"
```

Use the actual attempt suffix if not 1. Locate `client-ablation-plan.json` and
read the experiment/plan/topology, preflight-gate, per-arm native rows, sample-
validation, summary-integrity and summary. Completion requires successful job,
both preflight arms Passed with four accepted Tier1 targets each, 12 raw/host/
tick records, Passed evidence validation, no failed/incomplete summary records,
and one fixed-scene summary with six pairs. Do not treat Tier0 as Tier1 or missing
native as zero. Read every paired ratio and host observation, not just means.

If default PGO now reproduces adverse tails while FullOpts did not, the evidence
supports a tiering-dependent Client increment under common candidate DLLs;
inspect actual Tier1 client iterator native/control flow before proposing a
product correction. If null/mixed, retain the original NoGo and do not infer
full-product equivalence. If a gate fails, preserve all artifacts and fix that
specific problem without extra samples or weakened targets. Any new timing
requires approval. If work crosses 15:00, hand over the run ID/status and these
commands; do not cancel or launch replacement work merely for the handoff.
