# Hosting registration regression evidence

`run.py` overlays the same eleven-case fixture on two historical products and the
current checkout. The historical arms must reproduce their original three and
four failures. The current arm must pass the focused checks and every case in the
complete unit suite, with zero failures or skips and unchanged binary hashes.

The expected ordinary complete-suite inventory is **2,252**: the freshly
validated 2,212-case ColdStop base plus 40 complete-method-facts cases, including
all five queued RPC shapes with replacement/removal controls. The lease-race
runner adds its unchanged eight controls, for an expected 2,260. These combined
values are source-attributed expectations, not claims that the new C0 full suites
have run; fresh native discovery and explicit-probe attribution remain required.

The ColdStop base was validated by [PR #970](https://github.com/SunSi12138/SharpLink/pull/970):
three-platform ordinary suites of 2,212, plus the canonical lease-race injected
suites of 2,220. Those results do not validate this additional method-facts delta.
Earlier C0 local discovery reported 2,239 including five explicit probes, but
those raw artifacts were lost during workspace replacement. See both the
[method-facts status](../../doc/generated-method-facts-validation-status.md) and
[ColdStop status](../../doc/cold-stop-absolute-budget-validation.md).

The focused eleven-case Hosting fixture and historical controls are unchanged.
Real-TCP integration cases do not enter this unit census. The 18 ColdStop cases
are already included in the 2,212-case base, not counted twice. This candidate
does not include the independent six-case send-pump cleanup addition; a later
combined candidate must account for it separately. The previous 2,123-case count
predated subsequent unit additions: [run 39962583418](https://github.com/SunSi12138/SharpLink/actions/runs/39962583418)
executed 2,169 cases with zero failures/skips, then correctly rejected the stale
inventory expectation. This count correction preserves the eleven-case fixture,
both historical negative controls, exact count equality, parallelism, timeouts,
failure/skip requirements and binary-identity checks. It changes no product code.

Keep the count tied to the actual complete suite when tests are added or removed;
do not replace exact equality with a lower bound or infer the expected count from
the very run being checked. Preserve failed attempts and their artifacts.

In a runner environment with `RUNNER_TEMP` set, reproduce with:

```bash
DOTNET_CLI_UI_LANGUAGE=en TUNIT_MAX_PARALLEL_TESTS=8 \
  python3 eng/hosting-registration-regression/run.py \
  --out artifacts/hosting-registration-regression
```

Choose a fresh output directory for each attempt. English CLI output is required
by the existing summary parser. Successful historical negative controls are
expected test failures, not product passes.
