# OneWay shape local evidence (investigation only)

This harness compares exact baseline `553854f56c33a6e02ef225c24f558e5e92037aa1`
(A), an internal plain/streamed core split (C), and generated specialized entries (B).
Apply the C and B patches independently to the same baseline. Do not stack them.
Build the identical `test/SharpLink.OneWayLocalEvidence` source in each arm.
The generator runs from that arm: B genuinely exercises its new generated entries.

## Scope

The measured operation is an actual generated proxy invocation through the production
`SharpLinkClient` entry, admission, request serialization, outbound publication,
client-stream producer state machines, pending lease arbitration and completion.
The connected test transport supplies a real handshake/contract manifest and parses
outbound frames. There is no RPC server or network round trip. A sink checks the
serialized business value equals 42, request OneWay flags, request counts, stream
completion counts, and a published checksum. No production hooks are added.
The assembly reuses the existing `SharpLink.Benchmarks` friend name only to create
the protocol handshake fixture; this project is not loaded beside that benchmark app.

Six scenarios cover 0/1/2 stream writers:

- `sync0`: plain OneWay, no deadline; synchronous generated invocation completion.
- `sync1`, `sync2`: one or two empty client streams through the real generated writer
  and actual `ClientConnection.SendClientStreamAsync`; synchronous completion.
- `async1`, `async2`: reusable external stream gates force MoveNext suspension.
  The invocation must return incomplete. Releasing gates on the driver thread
  resumes real state machines and must complete the invocation before consumption.
  These isolate async lowering/completion work without a thread-pool hop per item.
- `async0`: plain OneWay with a 60-second method deadline. Its production emission
  wait is held in a transport FlushAsync gate. The driver waits until the gate is
  observed by FlushAsync before releasing it, and invocation completion is asserted
  asynchronous. It includes deadline machinery, scheduler work and one fixture TCS
  per operation; it is not directly comparable to the untimed `sync0` shape.

Empty streams intentionally isolate writer/state-machine overhead. They still send
one request and one StreamComplete per writer. They do not represent stream-item
serialization, flow-control, network or server throughput. Loop dispatch, assertion
branches, controlled-source release, and (async0) gate coordination are included in
elapsed time. Final send-pump drain and post-batch count checks are excluded. The
send pump runs concurrently with calls, so timing is still sensitive to scheduling.

## Measurement

Each subprocess performs 16,384–65,536 untimed warmup operations, collects GC, then
executes the requested measured operation count in batches. `ns_per_op` is the sum
of timed batch intervals divided by operation count. `bytes_per_op` measures total
process allocated bytes including background send-pump work and final drain.
`caller_thread_bytes_per_op` additionally isolates allocation inside timed invocation
batches for sync0/1/2 and async1/2. Same-thread completion is asserted. It is null for
async0, which changes threads. Sender buffer-pool growth can affect total bytes;
the caller-thread number is preferable for attributing state-machine changes.

The paired runner uses a new independent process per arm/scenario/round, rotates
all six three-arm orders, shuffles scenario order with recorded seed, and saves
its full schedule before execution. All attempted samples retain command, exit
status, stdout, stderr and JSON row, including failed/time-out attempts. A failed
scenario invalidates its qualification and receives no paired performance ratio.
The output directory must be new. Source equality across arms is verified, and
source diff plus binary and harness hashes are recorded. Each pair is one launch
in the same round, not one of the many loop iterations. The report gives median
ratios and seeded paired bootstrap intervals, not an end-to-end claim.

Default controlled runs disable tiering, PGO and ReadyToRun to avoid measuring
transitioning tiers. `--tiered 1` provides tiered/PGO corroboration (ReadyToRun remains
disabled). CPU count is a runtime configuration, not physical core pinning. Record
host/affinity externally if needed. Do not run builds, tests or competing benchmarks
while collecting timing evidence. Recheck promising results using tiered PGO and
on the target hardware before making broad performance claims.

## Reproduction

For each checkout:

```sh
dotnet restore test/SharpLink.OneWayLocalEvidence/SharpLink.OneWayLocalEvidence.csproj
dotnet build test/SharpLink.OneWayLocalEvidence/SharpLink.OneWayLocalEvidence.csproj \
  -c Release --no-restore -m:1 -p:UseSharedCompilation=false
```

When network restore is unavailable, a local-only cleared package-source config
can restore from a complete pre-existing package cache with `--disable-parallel
-m:1 -p:BuildInParallel=false -p:NuGetAudit=false`. This is not an audit pass. Keep
normal restore/audit enabled on connected CI. A workspace-local `TMPDIR` avoids
small `/tmp` tmpfs limits.

```sh
python3 eng/perf/run-oneway-local.py \
  --arm A=/path/baseline --arm C=/path/internal-split --arm B=/path/static-entries \
  --dotnet /path/dotnet --rounds 12 --iterations 200000 --batch 256 \
  --output /path/new-evidence-directory
```

Artifacts: `manifest.json`, `schedule.json`, append-only attempted `raw.jsonl`,
per-attempt `.stdout`, `.stderr`, `.status.json`, and `summary.json`. The source is
investigation-only, not part of the main solution or production package.
