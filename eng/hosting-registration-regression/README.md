# Hosting registration regression evidence

`run.py` overlays the same eleven-case fixture on two historical products and the
current checkout. The historical arms must reproduce their original three and
four failures. The current arm must pass the focused checks and every case in the
complete unit suite, with zero failures or skips and unchanged binary hashes.

The expected ordinary complete-suite inventory is **2,234**: the prior 2,194
cases plus 40 complete-method-facts cases, including all five queued RPC shapes
with replacement/removal controls. The lease-race runner adds its unchanged eight
controls, for an expected 2,242. These are source-attributed guard values, not
claims that either full suite has run. Prior local discovery reported 2,239 tests
including the same five explicit probes, but those raw artifacts were lost during
workspace replacement; fresh discovery must verify the reconstructed candidate.
See [the recovery and validation status](../../doc/generated-method-facts-validation-status.md).
The focused eleven-case Hosting fixture and historical controls are unchanged.
Real-TCP integration cases do not enter this unit census. This candidate does not
include independent ColdStop or six-case send-pump cleanup additions; a combined
candidate must account for those separately. The previous 2,123-case count
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
