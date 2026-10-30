# O203N4: bounded official 2.0.3 normal server-stream comparison

This is a diagnostic validation tool, not a product change or release gate.
The reviewed O203N3 tools at `9b78dea198ad7ce7da3cf30da6d05759e48f34cf`
are reused with exact candidate identities and temporary report retention updated.

## Identities and symmetric fixture

- Official: published 2.0.3 packages, source `4f9295ffb37ce68985051c11bfd9afc0e23166f3`, generated API 4.
- Candidate: source `8ae9ee36ebec82f82e03185432601a830c865e95`, tree `1f0c9291c7394d012d3241a9cf31482b0734b697`, package 3.0.0, generated API 5.
- SDK 10.0.102, private .NET and ASP.NET runtime 10.0.14, Ubuntu 24.04.
- Each arm uses its own package DLLs and generator; the business source, fixture,
  dependency identities, worker/connection count and runtime are common.
- Loopback TCP client and server share one process, one worker and one connection.
  Static and dynamic generated intercepted paths are checked positively. There is
  no cross-major interoperation test or claim of physical-core isolation.

## Frozen finite matrix and budget

| Phase | Arms | Cells | Fresh processes |
| --- | --- | --- | ---: |
| Strict cleanup | Candidate only | static/dynamic × 1/100 items | 4 |
| Normal validation | Both | static/dynamic × 1/100 items | 8 |
| Normal measurement | Both, six paired rounds | static/dynamic × 1/100 items | 48 |

Process order is unchanged from O203N3: four strict gates, eight normal gates,
then six pairs with scenarios rotated by pair index. Odd pairs run official then
candidate; even pairs run candidate then official. `phase-schedule.json` and each
host record retain exact zero-based launch indices 0–59.

Each measurement process warms for 3 seconds and measures for 6 seconds: 144
seconds of warmup plus 288 seconds of measurement (432 seconds nominal workload),
plus startup, normal controls, drain, shutdown, identity checks and builds.
The existing 120-second process timeout, 15-second cleanup guard and 90-minute
whole-job cap remain unchanged. The whole-job cap can terminate an incomplete
campaign before all individual timeout budgets expire; incomplete evidence fails
closed. No retry, replacement, discarded round, adaptive stop or additional
matrix is authorized by this protocol. Failed prerequisite gates prevent timing;
unsafe surviving process groups prevent further launches.

## Metrics and interpretation

QPS counts completed streams, not individual items. P99 is the exact nearest-rank
quantile of per-stream integer stopwatch durations. Report every round in launch
order, including QPS, P99 microseconds and process-wide allocation bytes per
completed stream. Allocation uses the existing two
`GC.GetTotalAllocatedBytes(true)` collection points enclosing the measurement
window. It includes client, server and measurement orchestration allocations,
not thread-local attribution; it excludes warmup, preallocated sample arrays,
drain/shutdown and serialization. Integer start/end/delta counters are retained
in both sample and raw-tick records and checked independently before deriving
exact rational B/op. Allocation has no pass/fail target.

Retain the unchanged six paired candidate/official QPS and P99 ratios per cell,
their separate medians, and distance to QPS +10% / P99 -20% targets. Any failed,
missing or invalid input suppresses all target conclusions. No pooled quantiles,
best-round selection, causal/confidence claim or final release acceptance.

## Activation and temporary reports

The tool commit must contain dormant `REQUEST.json`. Only after independent
review PASS and root release may a single request-only child be pushed to
`validation/official203-normal-limited-20261030-r1` with message
`run: official203 normal comparison O203N4`. It must name the reviewed parent
commit/tree. The first-attempt-only push gate rejects altered identities,
force-pushes, multi-commit activation, retries and unreviewed workflow changes.
The tool-parent push is dormant; it is not an activation request.

User-authorized temporary CI artifacts retain raw rounds/ticks, exact allocation
counters, process order, own-arm executable/library/generator snapshots, generated
source, provenance, build logs and failure files for **3 days**. Evidence is never
committed to this repository and no evidence branch is created. Runtime archive
hash pins and per-loaded-assembly provenance remain part of the evidence; large
SDK/runtime download archives and dependency caches are not uploaded. Download
reports within retention if downstream independent recomputation is needed.
An upload failure is a workflow failure and must not be described as retained.

Local validation is offline Python only:

```sh
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover \
  -s eng/official-normal-comparison -p 'test_*.py'
```

It checks the finite protocol, request gate, synthetic positive/negative identity
and raw-data controls. It does not compile or execute the C# fixture and is not
performance evidence. Real build/runtime verification belongs to the one
explicitly released hosted campaign.
