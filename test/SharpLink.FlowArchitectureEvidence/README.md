# Issue #735: real generated RPC architecture evidence

This is an independent, deliberately small generated RPC falsification screen. It
uses the same measurement sources and generated codec contract on both A/B arms.
Two populations are kept separate: the unchanged existing `IBenchmarkRpc` byte-array
contract is the fallback control, and the normally generated `IGrantBenchmarkRpc`
DTO contract supplies a verified exact-size codec for the server-sized B2 pilot.
Both ends validate complete payloads and exact stream counts. TCP, SharedMemory,
JIT PGO OFF/ON and published/executed NativeAOT are supported, with Unary, Server,
Client and Duplex shapes. Existing tests, defaults, windows, memory budgets,
SendPump and wire protocol are unchanged.

## Build identity before compilation

The committed `ModeIdentity.cs` contains deliberately invalid `UNCONFIGURED`
values. A run refuses to start until the collector stamps source identities and
then compiles that exact checkout. Command-line values can only verify the
compiled identity; they cannot relabel a previously built binary.

Use independent baseline/candidate worktrees with the same measurement directory.
Initial dev is `f8ba89121c87a6410a0139c44d7db30eab877f41`, including #840 and #841.
The actual matched baseline is the separate correctness-only control
`c7ba1768f31ce7219dc438dc6009347de6806ed5`: lease capture, snapshot ownership and
error-terminal late credit return. Those fixes appear identically in both arms
and cannot count as B2 gains. The stamper verifies the two exact control files
against initial dev, then every candidate production source against the reviewed
four-file `B2ProductionManifest.json`. Supply the actual commit tree SHA:

```bash
python3 test/SharpLink.FlowArchitectureEvidence/stamp_identity.py \
  --root . \
  --baseline-sha "$baseline_sha" \
  --candidate-sha "$candidate_sha" \
  --runtime-source-tree-sha "$runtime_tree_sha" \
  --mode correctness-fixed-baseline \
  --record "$artifact_directory/build-identity.json"
```

Allowed modes are `correctness-fixed-baseline` and `B2-server-grant-4096`.
The baseline is compiled from the exact control commit; the candidate is compiled
from the measured checkout commit. The same new harness directory is copied into
both disposable worktrees. No production source is patched by the stamper.
Every working source, committed source tree, linked original fixture, build policy
and actual candidate diff must match. Neither arm may include directional lock
splitting or a changed serializer/SendPump. The source manifest must be reviewed
and updated if the candidate changes.

The measurement content digest covers local C# files, project and stamping code,
linked source inputs and central build/package policy. `ModeIdentity.cs` is
excluded to avoid a circular digest and because its varying contents are metadata;
the record includes its separate hash. Every run reports this compiled identity,
executed app/binary SHA-256, and (JIT) the runtime assembly SHA-256. Retain the build
log, stamp record, executable/publish output, environment, exit code and run logs.

Build JIT with `-p:PublishAot=false`; publish AOT with `-p:PublishAot=true` and an
explicit RID. The harness emits and cross-checks both the compile-time AOT marker
and the executed `RuntimeFeature.IsDynamicCodeSupported/IsDynamicCodeCompiled`
flags. Do not use a managed `dotnet run` result as NativeAOT evidence.

## Invocation (11 positional arguments)

```text
<tcp|sharedmemory> <Unary|Server|Client|Duplex> <items> <bytes>
<concurrency> <minimum-warmup-ops-per-worker> <measured-ops-per-worker>
<baseline-sha> <runtime-source-tree-sha> <mode> <output-file>
```

The collector must impose its existing 60-second external process deadline and
retain timeout exits, `.started.txt` and stdout/stderr even when a final result
cannot be written. The client request-timeout disabling and SharedMemory
heartbeat 5 min / 10 min policy are inherited from `BenchmarkEnvironment` and
disclosed in every result. This is a matched benchmark policy, not timeout or
cancellation acceptance coverage. Windows, protocol and flush options stay at the
actual runtime defaults and resource settings are reported.

`FLOW_EVIDENCE_PAYLOAD_KIND` is required: `sized-dto` or `byte-array`. Unary
requires `items=1`; stream shapes allow `items=0`. For byte-array, the size means
application array bytes; encoded items include the codec's four-byte length.
For sized-dto, the size means the entire encoded DTO: the generated string/ushort
format is verified by the actual manifest codec before measurement, and its
preflight requires exact-size support, sized/regular byte identity and a complete
roundtrip. The 16/64/4096 encoded sizes correspond to 3/27/2043 UTF-16 code units
plus the remaining length/tag fields. These denominators are reported separately;
a 16 B DTO is not labelled a 16-byte application array.

Each item repeats a deterministic payload, so this screen detects corruption and
missing/extra items but is not an independent item-reordering test. No codec is
substituted in only one arm. Both contracts are generated in the same assembly;
both unary pre/post session probes must observe one physical connection.

## Measurement boundaries and outputs

One persistent task per worker drives one outstanding RPC at a time on the same
physical connection. Workers, closures and latency arrays are created before
measurement. Each worker warms until it has met **both** its minimum operation
count and a five-second duration; actual warmup counts/duration are recorded.
This gives tiered PGO a warmup interval but does not establish tier stabilization.
All workers then wait at one start gate. Warmup completion and call/stream cleanup
settle before GC and the measurement counters/start gate; each measured RPC has
its own start and validated-completion timestamp. Measurement uses a fixed
operation count per worker and includes the common gate-release/tail overhead.

The process measures client + server + full validation together. A fixed
ThreadPool minimum `max(32, processorCount * 4)` is applied identically to both
sides; original, configured, maximum and observed thread counts are recorded.
CPU and allocation deltas are process-wide. GC collection counts and total pause
delta, ThreadPool completed-work-item delta, RPC/s, logical item/s, allocation and
CPU per RPC/logical item, and nearest-rank P50/P95/P99/P99.9/max are emitted. The
upper-tail sample count is explicit: short screens cannot establish P99.9 safety.

Two cost boundaries are retained. The existing `elapsedSeconds`, throughput,
CPU, allocation and GC fields end when all measured RPCs have completed and their
payloads/counts have been validated. Additional `settled*` fields extend from the
same measurement start until `WaitForIdleAsync` observes zero pending/active
calls and client streams. The latter include lifecycle work after caller
completion, the polling/Task.Delay costs of `WaitForIdleAsync` itself, and the
intermediate completed-counter observations. These observer costs are disclosed
and are not estimated away. Neither boundary includes raw sample/report I/O,
the additional session probe, or transport disposal. The physical-session probe
and its subsequent idle check occur after both sets of counters are captured.
`completedToSettledMicroseconds` records the whole elapsed difference between
these boundaries; it must not be treated as pure production cleanup time.

`items` means logical workload items. Duplex produces two stream-payload items per
logical item; Unary has two application payload transits but zero StreamData
items. Separate fields report these denominators. Actual wire DATA frame count,
wire byte count, total locks/RMWs/writer handoffs, ready-queue depth and prepared
budget peaks remain explicitly unmeasured; they are never inferred from payload
or ThreadPool counts. Eight production B2 path counters are reported as measured
warmup-to-settlement deltas: activated/disposed streams, first-probe fallback,
local items, owner refills, exact slow attempts, recalled grant and refunded-item
bytes. Per-item counters use the existing local stream lock and aggregate on
Dispose; their cost is included in the candidate. The parser requires eligible
server/duplex DTO calls to account for activation or first-probe fallback, all
activated leases to be disposed, and small long streams to demonstrate real local
items/refills. Baseline, byte-array, Unary and Client control arms must have all
B2 counters zero. A 4 KiB item need not have local grant hits.

Files beside `<output-file>`:

- `.started.txt`: compiled identity and start status, before transport setup.
- `.samples.csv`: every measured worker/RPC start, end and latency, without batch
  latency substitution; written after settled cost observations and before final
  session-probe validation.
- `.workers.csv`: completion totals, latency/spans, failure flags and actual
  warmup count/duration by persistent worker.
- `.fairness.csv`: worker completion counts and Jain index at common 10%, 25%,
  50%, 75%, 90% and 100% wall-time boundaries, derived after measurement without a
  sampling observer on the hot path. The 100% balance is forced by fixed counts;
  earlier points and per-worker spans are the useful observations.
- `.failure.txt` / `.worker-N.failure.txt`: full exceptions. Each worker writes its
  original exception immediately in its failure handler, before notifying the
  coordinator, so a different pending RPC or blocked teardown cannot hide it.

The measurement coordinator waits for either all workers or the first worker
failure. This single completion race is allocated before measurement; the normal
per-RPC loop has no added task or wait. On failure, the coordinator cancels worker
gates and enters existing transport disposal to terminate pending RPCs. The
collector deadline remains unchanged. If artifact writing itself fails, the
original worker exception and artifact error are written to stderr.

Before and after measurement, a generated unary probe observes the server session
ID. The harness requires one ready physical connection, an unchanged ID, and zero
pending calls, active calls and client streams after settlement. Success is written
only after environment disposal has completed. Server/client Build calls are
inside startup cleanup handling. If a later Build or startup step fails, already
created resources are cleaned up. Client disposal, server stop and server disposal
are each attempted even if an earlier step throws. A startup/measurement failure
is retained as the first exception when cleanup also fails; all cleanup exceptions
are retained with their stage labels in the aggregate. The existing zero server
stop grace interval and external collector deadline are unchanged. These checks do not replace
deterministic lifecycle/credit tests or prove that every internal byte budget is
settled; no such acceptance claim is emitted.

## Evidence scope

All modes continue to use `dev-SendPump` and report `b3ReadyWriterEnabled=false`.
Only the candidate compiles the harness's B2 evidence bridge; the production pilot
itself has no optional fallback compile flag. `b2ProducerGrantEnabled` describes
the compiled candidate and path counters prove actual workload activation. The
source-verifying parser rejects a silently unused eligible path.

These are real generated RPC measurements for the bounded server-sized B2 pilot,
not B3, a microkernel or full production acceptance. Absolute numbers must not be
compared directly with a different historical harness, different DTO/byte-array
population or a different measurement version. A/B share measurement hash,
payload policy, defaults, runtime, machine, affinity and deadline. Interleaved
AB/BA and A/A samples, raw per-RPC observations, failures and all source/binary
identities are retained. Every matrix job depends on all 25 deterministic,
original-suite and mutation gates for the exact same measured SHA. Collection
success is not Go; a stop after any correctness/progress failure remains evidence.
The report includes `fullAcceptanceGate=false-stage1-throughput-evidence-only`.
