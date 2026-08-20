#!/usr/bin/env bash
set -euo pipefail

mkdir -p artifacts/issue-263/perf/before artifacts/issue-263/perf/after

echo '== restore baseline =='
dotnet restore Sharplink.slnx

echo '== build benchmark baseline =='
dotnet build test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj -c Release --no-restore -v minimal

for n in 1 2 3; do
  dotnet run -c Release --no-build --project test/SharpLink.Benchmarks -- \
    --feature-evidence client FixedDefault 500 0.75 100000 \
    "artifacts/issue-263/perf/before/client-fixed-${n}.json"
  dotnet run -c Release --no-build --project test/SharpLink.Benchmarks -- \
    --feature-evidence server StaticDefault 500 0.75 100000 \
    "artifacts/issue-263/perf/before/server-static-${n}.json"
done

echo '== apply candidate =='
base64 -d .github/issue-263.patch.gz.b64 | gzip -d > /tmp/issue263.patch
git apply --check /tmp/issue263.patch
git apply /tmp/issue263.patch
git diff --check

echo '== formatting =='
dotnet format whitespace Sharplink.slnx --no-restore --verify-no-changes --verbosity minimal

echo '== release build =='
dotnet build Sharplink.slnx --no-restore -c Release -v minimal

echo '== unit tests =='
dotnet test --project test/SharpLink.UnitTests/SharpLink.UnitTests.csproj -c Release --no-build

echo '== generator tests =='
dotnet test --project test/SharpLink.Generator.Tests/SharpLink.Generator.Tests.csproj -c Release --no-build

echo '== integration tests =='
dotnet run -c Release --no-build \
  --project test/SharpLink.IntegrationTests/SharpLink.IntegrationTests.csproj \
  -- --maximum-parallel-tests 1 --timeout 120s

echo '== candidate performance evidence =='
for n in 1 2 3; do
  dotnet run -c Release --no-build --project test/SharpLink.Benchmarks -- \
    --feature-evidence client FixedDefault 500 0.75 100000 \
    "artifacts/issue-263/perf/after/client-fixed-${n}.json"
  dotnet run -c Release --no-build --project test/SharpLink.Benchmarks -- \
    --feature-evidence client ClientInterceptorDynamicDisabled 500 0.75 100000 \
    "artifacts/issue-263/perf/after/client-dynamic-disabled-${n}.json"
  dotnet run -c Release --no-build --project test/SharpLink.Benchmarks -- \
    --feature-evidence server StaticDefault 500 0.75 100000 \
    "artifacts/issue-263/perf/after/server-static-${n}.json"
  dotnet run -c Release --no-build --project test/SharpLink.Benchmarks -- \
    --feature-evidence server ServerInterceptorDynamicDisabled 500 0.75 100000 \
    "artifacts/issue-263/perf/after/server-dynamic-disabled-${n}.json"
done

dotnet run -c Release --no-build --project test/SharpLink.Benchmarks -- \
  --feature-evidence client ClientInterceptorDynamicEnabled 500 0.75 100000 \
  artifacts/issue-263/perf/after/client-dynamic-enabled.json
dotnet run -c Release --no-build --project test/SharpLink.Benchmarks -- \
  --feature-evidence client ClientAndServerInterceptorDynamicEnabled 500 0.75 100000 \
  artifacts/issue-263/perf/after/client-server-dynamic-enabled.json
dotnet run -c Release --no-build --project test/SharpLink.Benchmarks -- \
  --feature-evidence client ClientInterceptorAfterManyReplacements 500 0.75 100000 \
  artifacts/issue-263/perf/after/client-after-replacements.json
dotnet run -c Release --no-build --project test/SharpLink.Benchmarks -- \
  --feature-evidence server ServerInterceptorDynamicEnabled 500 0.75 100000 \
  artifacts/issue-263/perf/after/server-dynamic-enabled.json
dotnet run -c Release --no-build --project test/SharpLink.Benchmarks -- \
  --feature-evidence server ServerInterceptorAfterManyReplacements 500 0.75 100000 \
  artifacts/issue-263/perf/after/server-after-replacements.json

echo '== disabled-path performance gate =='
python3 - <<'PY'
import glob
import json
import statistics


def median(pattern, key):
    rows = []
    for path in glob.glob(pattern):
        with open(path, encoding='utf-8') as stream:
            rows.append(json.load(stream)[key])
    if not rows:
        raise SystemExit(f'no evidence for {pattern}')
    return statistics.median(rows)


cases = [
    ('client',
     'artifacts/issue-263/perf/before/client-fixed-*.json',
     'artifacts/issue-263/perf/after/client-fixed-*.json',
     'artifacts/issue-263/perf/after/client-dynamic-disabled-*.json'),
    ('server',
     'artifacts/issue-263/perf/before/server-static-*.json',
     'artifacts/issue-263/perf/after/server-static-*.json',
     'artifacts/issue-263/perf/after/server-dynamic-disabled-*.json'),
]
failed = False
lines = []
for name, before, after, dynamic in cases:
    before_t = median(before, 'throughputPerSecond')
    after_t = median(after, 'throughputPerSecond')
    dynamic_t = median(dynamic, 'throughputPerSecond')
    before_a = median(before, 'allocatedBytesPerOperation')
    after_a = median(after, 'allocatedBytesPerOperation')
    dynamic_a = median(dynamic, 'allocatedBytesPerOperation')
    regression = (before_t - after_t) / before_t * 100.0
    dynamic_delta = (after_t - dynamic_t) / after_t * 100.0
    line = (
        f'{name}: before={before_t:.0f} op/s after={after_t:.0f} op/s '
        f'regression={regression:.2f}% dynamic-disabled={dynamic_t:.0f} op/s '
        f'delta-vs-after={dynamic_delta:.2f}% '
        f'alloc={before_a:.3f}->{after_a:.3f}/{dynamic_a:.3f} B/op')
    print(line)
    lines.append(line)
    if regression > 5.0:
        print(f'ERROR: {name} disabled hot path regressed {regression:.2f}% (>5%)')
        failed = True
    if after_a - before_a > 2.0:
        print(f'ERROR: {name} baseline allocation increased by {after_a-before_a:.3f} B/op')
        failed = True
    if dynamic_a - after_a > 2.0:
        print(f'ERROR: {name} runtime-disabled allocation exceeds patched baseline by {dynamic_a-after_a:.3f} B/op')
        failed = True

with open('artifacts/issue-263/perf/summary.txt', 'w', encoding='utf-8') as stream:
    stream.write('\n'.join(lines) + '\n')
if failed:
    raise SystemExit(1)
PY

echo '== commit validated candidate =='
git config user.name 'github-actions[bot]'
git config user.email '41898282+github-actions[bot]@users.noreply.github.com'
git add -- \
  doc/index.md \
  doc/runtime-interceptors.md \
  src/SharpLink.Abstractions/ISharpLinkClient.cs \
  src/SharpLink.Abstractions/ISharpLinkServer.cs \
  src/SharpLink.Abstractions/SharpLinkInterceptors.cs \
  src/SharpLink.Client/SharpClientBuilder.cs \
  src/SharpLink.Client/SharpLinkClient.Interceptors.cs \
  src/SharpLink.Client/SharpLinkClient.Invokers.cs \
  src/SharpLink.Client/SharpLinkClient.RuntimeInterceptors.cs \
  src/SharpLink.Client/SharpLinkClient.Telemetry.cs \
  src/SharpLink.Client/SharpLinkClient.cs \
  src/SharpLink.Server/SharpLinkServer.Interceptors.cs \
  src/SharpLink.Server/SharpLinkServer.RuntimeInterceptors.cs \
  src/SharpLink.Server/SharpLinkServer.cs \
  src/SharpLink.Server/SharpLinkServerBuilder.cs \
  test/SharpLink.Benchmarks/BenchmarkEnvironment.cs \
  test/SharpLink.Benchmarks/FeatureBenchmarkScenarios.cs \
  test/SharpLink.IntegrationTests/DynamicInterceptorIntegrationTests.cs
git rm -- .github/issue-263.patch.gz.b64

git diff --cached --name-status
git commit -m 'feat: support runtime interceptor replacement'
git push origin HEAD:agent/issue-263-dynamic-interceptors

git rev-parse HEAD > artifacts/issue-263/validated-head.txt
