# Hosting registration regression evidence

`run.py` overlays the same eleven-case fixture on two historical products and the
current checkout. The historical arms must reproduce their original three and
four failures. The current arm must pass the focused checks and every case in the
complete unit suite, with zero failures or skips and unchanged binary hashes.

The expected complete-suite inventory is **2,473**, derived from the exact source
case additions below, not runtime discovery. The old 2,171 expectation already
omitted dev's 17 quiescent-pump controls; they are not new in this integration.
The eleven-case Hosting fixture, historical negative controls, exact count
equality, parallelism, timeouts, failure/skip and binary checks remain unchanged.

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

## Combined correctness source census

The exact ordinary expectation is **2,473**: dev `57f06f17` **2,188** + required
physical ownership **111** + cooperative maintenance **157** + static-stream
flags **6** + send-pump cleanup **6** + shutdown composition **5**. The last five
are three runtime output-safety/reentry cases and two server graceful-budget cases.
No case declarations are removed. Historical #931's 113 includes two queued-reader
cases already on dev; the five preexisting Explicit workers remain outside ordinary
execution. The lease runner alone adds its unchanged eight injected cases (**2,481**).

This expectation comes from source path/method/Arguments identities, not a convenient
run result. It is **not** final-head runtime discovery, execution or performance
acceptance. Fresh authorized execution must confirm the exact identity set with zero
drops/skips; old-negative and focused counts, markers, timeouts, parallelism and
acceptance rules are unchanged.
