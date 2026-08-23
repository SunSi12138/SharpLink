#!/usr/bin/env bash
set -euo pipefail

: "${BASE_SHA:?BASE_SHA is required}"
: "${HEAD_SHA:?HEAD_SHA is required}"

cat > /tmp/issue305-patch-loadtest.py <<'PY'
from pathlib import Path
path = Path('test/SharpLink.LoadTest/Program.cs')
text = path.read_text()
old_validation = '''if (admissionMode is not ("disabled" or "immediate" or "queue" or "reject"))'''
new_validation = '''if (admissionMode is not ("disabled" or "immediate" or "partition-immediate" or "queue" or "reject"))'''
old_switch = '''            "immediate" => builder.UseAdmissionControl(admission =>\n                admission.Global.UseConcurrency(4096)),\n            "queue" => builder.UseAdmissionControl(admission =>'''
new_switch = '''            "immediate" => builder.UseAdmissionControl(admission =>\n                admission.Global.UseConcurrency(4096)),\n            "partition-immediate" => builder.UseAdmissionControl(admission =>\n                admission.UsePartition(\n                    static _ => "hot",\n                    partition =>\n                    {\n                        partition.MaxPartitions = 1;\n                        partition.UseConcurrency(4096);\n                    })),\n            "queue" => builder.UseAdmissionControl(admission =>'''
if old_validation not in text or old_switch not in text:
    raise SystemExit('load-test admission patch anchors changed')
text = text.replace(old_validation, new_validation, 1)
text = text.replace(old_switch, new_switch, 1)
path.write_text(text)
PY

rm -rf /tmp/issue305-c1-baseline /tmp/issue305-c1-candidate

git checkout --force "$BASE_SHA"
git clean -xffd
python3 /tmp/issue305-patch-loadtest.py
dotnet restore test/SharpLink.LoadTest/SharpLink.LoadTest.csproj
dotnet publish test/SharpLink.LoadTest/SharpLink.LoadTest.csproj -c Release --no-restore -o /tmp/issue305-c1-baseline

git checkout --force "$HEAD_SHA"
git clean -xffd
python3 /tmp/issue305-patch-loadtest.py
dotnet restore test/SharpLink.LoadTest/SharpLink.LoadTest.csproj
dotnet publish test/SharpLink.LoadTest/SharpLink.LoadTest.csproj -c Release --no-restore -o /tmp/issue305-c1-candidate

mkdir -p artifacts/issue-305-c1/runs
: > artifacts/issue-305-c1/formal-load.log

run_stage() {
  local binary="$1"
  local label="$2"
  local output="artifacts/issue-305-c1/runs/${label}.log"
  echo "=== $label ===" | tee -a artifacts/issue-305-c1/formal-load.log
  "$binary/SharpLink.LoadTest" \
    --mode local --transport tcp --operation add \
    --duration 15 --warmup 3 --concurrency 1 \
    --min-connections 1 --max-connections 1 \
    --request-timeout disabled --admission partition-immediate \
    --recording formal --maximum-recorded-operations 2000000 --metrics-port 0 \
    | tee "$output"
  local result
  result=$(grep '^\[Result\]' "$output" | tail -1)
  test -n "$result"
  echo "SAMPLE label=$label $result" | tee -a artifacts/issue-305-c1/formal-load.log
}

run_stage /tmp/issue305-c1-baseline baseline-r1
run_stage /tmp/issue305-c1-candidate candidate-r1
run_stage /tmp/issue305-c1-candidate candidate-r2
run_stage /tmp/issue305-c1-baseline baseline-r2
run_stage /tmp/issue305-c1-baseline baseline-r3
run_stage /tmp/issue305-c1-candidate candidate-r3
run_stage /tmp/issue305-c1-candidate candidate-r4
run_stage /tmp/issue305-c1-baseline baseline-r4
run_stage /tmp/issue305-c1-baseline baseline-r5
run_stage /tmp/issue305-c1-candidate candidate-r5

python3 - <<'PY'
import re
import statistics
from pathlib import Path

text = Path('artifacts/issue-305-c1/formal-load.log').read_text()
pattern = re.compile(
    r'SAMPLE label=(?P<label>\S+) \[Result\] op=add c=1 '
    r'qps=(?P<qps>[0-9.]+).*?p50=(?P<p50>[0-9.]+)us '
    r'p95=(?P<p95>[0-9.]+)us p99=(?P<p99>[0-9.]+)us .*?avg=(?P<avg>[0-9.]+)us')
rows = []
for match in pattern.finditer(text):
    row = match.groupdict()
    rows.append({
        'label': row['label'],
        'kind': 'candidate' if row['label'].startswith('candidate') else 'baseline',
        'qps': float(row['qps']),
        'p50': float(row['p50']),
        'p95': float(row['p95']),
        'p99': float(row['p99']),
        'avg': float(row['avg']),
    })

if len(rows) != 10:
    raise SystemExit(f'expected 10 formal c1 result rows, got {len(rows)}')

baseline = [row for row in rows if row['kind'] == 'baseline']
candidate = [row for row in rows if row['kind'] == 'candidate']
median = lambda values, key: statistics.median(row[key] for row in values)
bavg, cavg = median(baseline, 'avg'), median(candidate, 'avg')
bp50, cp50 = median(baseline, 'p50'), median(candidate, 'p50')
bp99, cp99 = median(baseline, 'p99'), median(candidate, 'p99')
bqps, cqps = median(baseline, 'qps'), median(candidate, 'qps')
avg_delta = (cavg / bavg - 1.0) * 100.0
p50_delta = (cp50 / bp50 - 1.0) * 100.0
p99_delta = (cp99 / bp99 - 1.0) * 100.0
qps_delta = (cqps / bqps - 1.0) * 100.0
gate = 'PASS' if avg_delta <= 2.0 else 'FAIL'

summary = f'''# Issue #305 formal c1 single-partition evidence

| Metric | Baseline median | A2 median | Delta |
| :--- | ---: | ---: | ---: |
| Average latency | {bavg:.2f} us | {cavg:.2f} us | {avg_delta:+.2f}% |
| P50 | {bp50:.2f} us | {cp50:.2f} us | {p50_delta:+.2f}% |
| P99 | {bp99:.2f} us | {cp99:.2f} us | {p99_delta:+.2f}% |
| QPS | {bqps:.0f} | {cqps:.0f} | {qps_delta:+.2f}% |

Gate: **{gate}** — evaluated on median average full-RPC latency across five interleaved 15-second samples per variant; #305 requires single-partition latency not to stably regress by more than 2%.
'''
Path('artifacts/issue-305-c1/summary.md').write_text(summary)
print(summary)
with open(Path(__import__('os').environ['GITHUB_STEP_SUMMARY']), 'a') as stream:
    stream.write(summary)
if gate == 'FAIL':
    raise SystemExit('Issue #305 formal c1 latency gate failed')
PY
