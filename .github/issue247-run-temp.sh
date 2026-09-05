#!/usr/bin/env bash
set -euxo pipefail

BASE_SHA="bb9e1643d091eaa5f07219e2fdc10c9b313c24ee"
FEATURE_BRANCH="codex/issue247-lazy-single-slot-request"
ARTIFACT_DIR="$GITHUB_WORKSPACE/artifacts/issue247-final"
mkdir -p "$ARTIFACT_DIR"

# Prove this branch contains only temporary evidence machinery before resetting it.
git fetch origin "$BASE_SHA" --depth=1
git diff --name-only "$BASE_SHA" HEAD | sort | tee "$ARTIFACT_DIR/temp-diff.txt"
test "$(git diff --name-only "$BASE_SHA" HEAD | wc -l)" -eq 5
grep -qx '.github/issue247-eval-temp.py' "$ARTIFACT_DIR/temp-diff.txt"
grep -qx '.github/issue247-pair-temp.cs' "$ARTIFACT_DIR/temp-diff.txt"
grep -qx '.github/issue247-run-temp.sh' "$ARTIFACT_DIR/temp-diff.txt"
grep -qx '.github/issue247-trace-parser-temp.cs' "$ARTIFACT_DIR/temp-diff.txt"
grep -qx '.github/workflows/issue247-run-temp.yml' "$ARTIFACT_DIR/temp-diff.txt"

cp .github/issue247-pair-temp.cs /tmp/Issue247PairEvidence.cs
cp .github/issue247-eval-temp.py /tmp/issue247-eval.py
cp .github/issue247-trace-parser-temp.cs /tmp/Issue247TraceParser.cs

git fetch origin tmp/issue247-evidence-20260905:refs/remotes/origin/issue247-old-temp --depth=20
git show refs/remotes/origin/issue247-old-temp:.github/issue247-apply-temp.py > /tmp/issue247-apply.py

echo "workflow_sha=$(git rev-parse HEAD)" | tee "$ARTIFACT_DIR/revision.txt"
echo "baseline_sha=$BASE_SHA" | tee -a "$ARTIFACT_DIR/revision.txt"
dotnet --info > "$ARTIFACT_DIR/dotnet-info.txt"
uname -a > "$ARTIFACT_DIR/uname.txt"
lscpu > "$ARTIFACT_DIR/cpu.txt"

git reset --hard "$BASE_SHA"
python3 /tmp/issue247-apply.py
git diff --check
git status --short | tee "$ARTIFACT_DIR/candidate-status.txt"
git diff -- src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs \
  test/SharpLink.UnitTests/Server/AdmissionControlTests.cs \
  > "$ARTIFACT_DIR/candidate.patch"
cp src/SharpLink.Server/Admission/SharpLinkAdmissionController.FastPath.cs \
  "$ARTIFACT_DIR/SharpLinkAdmissionController.FastPath.cs"

# Exact PR Fast source gates + unit tests.
dotnet restore Sharplink.slnx
python3 eng/check-project-reference-boundaries.py
dotnet format whitespace Sharplink.slnx --no-restore --verify-no-changes --verbosity minimal
python3 eng/test-verify-maintainability.py
bash eng/check-maintainability.sh
dotnet build Sharplink.slnx --no-restore -c Release -v minimal
./eng/verify-generated-assembly-dependencies.sh
dotnet test --project test/SharpLink.UnitTests/SharpLink.UnitTests.csproj -c Release --no-build \
  | tee "$ARTIFACT_DIR/unit-tests.log"

# Build exact base and candidate benchmark worktrees with the same uncommitted harness.
git worktree add --detach /tmp/issue247-base "$BASE_SHA"
for root in "$GITHUB_WORKSPACE" /tmp/issue247-base; do
  cp /tmp/Issue247PairEvidence.cs "$root/test/SharpLink.Benchmarks/Issue247PairEvidence.cs"
  ROOT="$root" python3 - <<'PY'
import os
from pathlib import Path
p = Path(os.environ['ROOT']) / 'test/SharpLink.Benchmarks/Program.cs'
s = p.read_text()
needle = '        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);'
replacement = '''        if (args.Length > 0 && string.Equals(args[0], "--issue247-pair", StringComparison.Ordinal))
        {
            await Issue247PairEvidence.RunAsync(args[1]);
            return;
        }
        if (args.Length > 0 && string.Equals(args[0], "--issue247-trace", StringComparison.Ordinal))
        {
            await Issue247PairEvidence.TraceAsync(args[1]);
            return;
        }
''' + needle
if needle not in s:
    raise SystemExit(f'Program insertion point missing in {p}')
p.write_text(s.replace(needle, replacement, 1))
PY
done

dotnet build /tmp/issue247-base/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj -c Release -v minimal
dotnet build test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj -c Release -v minimal

# Eleven alternating controller rounds: exact allocated bytes and elapsed CPU proxy on one runner.
mkdir -p "$ARTIFACT_DIR/pair"
for r in $(seq 1 11); do
  if (( r % 2 == 1 )); then order="base head"; else order="head base"; fi
  for side in $order; do
    if [[ "$side" == base ]]; then
      project=/tmp/issue247-base/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj
      output="$ARTIFACT_DIR/pair/base-${r}.json"
    else
      project=test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj
      output="$ARTIFACT_DIR/pair/head-${r}.json"
    fi
    dotnet run --no-build -c Release --project "$project" -- --issue247-pair "$output"
  done
done

# Existing queue/reject benchmark suite is the control for lock/queue behavior.
mkdir -p "$ARTIFACT_DIR/bdn-base" "$ARTIFACT_DIR/bdn-head"
dotnet run --no-build -c Release \
  --project /tmp/issue247-base/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj -- \
  --filter '*AdmissionControllerBenchmarks*' \
  --artifacts "$ARTIFACT_DIR/bdn-base" \
  | tee "$ARTIFACT_DIR/bdn-base.log"
dotnet run --no-build -c Release \
  --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj -- \
  --filter '*AdmissionControllerBenchmarks*' \
  --artifacts "$ARTIFACT_DIR/bdn-head" \
  | tee "$ARTIFACT_DIR/bdn-head.log"

# Five alternating full tiny-RPC rounds, including StaticDefault as a per-side baseline.
mkdir -p "$ARTIFACT_DIR/rpc"
for r in 1 2 3 4 5; do
  if (( r % 2 == 1 )); then order="base head"; else order="head base"; fi
  for side in $order; do
    if [[ "$side" == base ]]; then
      project=/tmp/issue247-base/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj
      prefix="$ARTIFACT_DIR/rpc/base"
      sha="$BASE_SHA"
    else
      project=test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj
      prefix="$ARTIFACT_DIR/rpc/head"
      sha=issue247-candidate
    fi
    SHARPLINK_BENCHMARK_SHA="$sha" dotnet run --no-build -c Release --project "$project" -- \
      --feature-evidence server StaticDefault 2000 2 1000000 "${prefix}-static-${r}.json"
    SHARPLINK_BENCHMARK_SHA="$sha" dotnet run --no-build -c Release --project "$project" -- \
      --feature-evidence server AdmissionImmediate 2000 2 1000000 "${prefix}-admission-${r}.json"
  done
done

# Baseline allocation stacks: sampling distinguishes SharpLink request/slot state from BCL leases.
dotnet tool install --global dotnet-trace
BASE_DLL="$(find /tmp/issue247-base/test/SharpLink.Benchmarks/bin/Release -name SharpLink.Benchmarks.dll -print -quit)"
test -n "$BASE_DLL"
for scenario in global-concurrency global-rate; do
  dotnet-trace collect --profile gc-verbose \
    --output "$ARTIFACT_DIR/${scenario}.nettrace" \
    -- dotnet "$BASE_DLL" --issue247-trace "$scenario"
done

mkdir -p /tmp/issue247-trace-parser
cd /tmp/issue247-trace-parser
dotnet new console --framework net10.0 --force
dotnet add package Microsoft.Diagnostics.Tracing.TraceEvent
cp /tmp/Issue247TraceParser.cs Program.cs
dotnet build -c Release
for scenario in global-concurrency global-rate; do
  dotnet run --no-build -c Release -- "$ARTIFACT_DIR/${scenario}.nettrace" \
    > "$ARTIFACT_DIR/${scenario}-allocation-stacks.tsv"
  test "$(wc -l < "$ARTIFACT_DIR/${scenario}-allocation-stacks.tsv")" -gt 1
done
cd "$GITHUB_WORKSPACE"

# Gate candidate. A failure stops before any production branch is pushed.
python3 /tmp/issue247-eval.py "$ARTIFACT_DIR" | tee "$ARTIFACT_DIR/gate-evaluation.log"

# Remove uncommitted benchmark harness and commit exactly the validated production/test delta.
git restore test/SharpLink.Benchmarks/Program.cs
rm -f test/SharpLink.Benchmarks/Issue247PairEvidence.cs
git diff --check
git add src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs \
  src/SharpLink.Server/Admission/SharpLinkAdmissionController.FastPath.cs \
  test/SharpLink.UnitTests/Server/AdmissionControlTests.cs
git diff --cached --name-only | sort | tee "$ARTIFACT_DIR/staged-files.txt"
test "$(git diff --cached --name-only | wc -l)" -eq 3
grep -qx 'src/SharpLink.Server/Admission/SharpLinkAdmissionController.FastPath.cs' "$ARTIFACT_DIR/staged-files.txt"
grep -qx 'src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs' "$ARTIFACT_DIR/staged-files.txt"
grep -qx 'test/SharpLink.UnitTests/Server/AdmissionControlTests.cs' "$ARTIFACT_DIR/staged-files.txt"

git config user.name "github-actions[bot]"
git config user.email "41898282+github-actions[bot]@users.noreply.github.com"
git commit -m "perf(admission): defer single-slot request state"
git rev-parse HEAD | tee "$ARTIFACT_DIR/candidate-commit.txt"
git push origin "HEAD:refs/heads/$FEATURE_BRANCH"
