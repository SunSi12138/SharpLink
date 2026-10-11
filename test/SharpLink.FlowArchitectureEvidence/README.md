# Issue #735: real generated RPC architecture evidence

This is an independent, deliberately small acceptance-screen harness. It links the
existing generated `IBenchmarkRpc` contracts, codec inputs, environment helpers and
fixture service, and replaces only the test service's payload methods so both ends
compare **all payload bytes and exact stream counts**. It supports TCP and
SharedMemory, JIT and published/executed NativeAOT, and `Unary`, `Server`, `Client`
and `Duplex` shapes. No existing test, production option, protocol, source table,
flow-control window, memory budget or SendPump flush policy is modified here.

## Build identity before compilation

The committed `ModeIdentity.cs` contains deliberately invalid `UNCONFIGURED`
values. A run refuses to start until the collector stamps source identities and
then compiles that exact checkout. Command-line values can only verify the
compiled identity; they cannot relabel a previously built binary.

Use independent baseline/candidate worktrees with the same measurement directory.
The baseline is `f8ba89121c87a6410a0139c44d7db30eab877f41`; it already contains
#840 and #841. The candidate commit must contain the reviewed D controller change.
Capture a tree **after** any source preparation and use its explicit SHA:

```bash
python3 test/SharpLink.FlowArchitectureEvidence/stamp_identity.py \
  --root . \
  --baseline-sha "$baseline_sha" \
  --candidate-sha "$candidate_sha" \
  --runtime-source-tree-sha "$runtime_tree_sha" \
  --mode baseline \
  --record "$artifact_directory/build-identity.json"
```

Allowed modes are `baseline`, `split-gate`, and `D-split-direction-gates`; the last
two name the same source-verified D control. The script is self-contained, so it
can run in the baseline worktree after copying this measurement directory. It
checks every production source path/blob against the recorded tree and baseline,
with only one allowed difference: the pinned, reviewed D controller SHA-256. It
also checks the unchanged linked fixtures and central build/package inputs. It
rejects a split-mode binary whose candidate commit does not contain those actual
production sources. The pinned D digest must be deliberately updated if D changes.

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

Unary requires `items=1`; stream shapes allow `items=0`. Arbitrary positive payload
sizes include 16 B, 64 B and 4 KiB. The 16 B / 4 KiB arrays use the existing payload
fixtures; other sizes retain their zero-interior fixture style. Each item has the
same payload, so this performance screen detects byte corruption and missing or
extra items but does not claim an independent item-reordering test.

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

`items` means logical workload items. Duplex produces two stream-payload items per
logical item; Unary has two application payload transits but zero StreamData
items. Separate fields report these denominators. Actual wire DATA frame count,
wire byte count, internal coordination counters, ready-queue depth and prepared
budget peaks are explicitly `not-instrumented`; they are never inferred from
payload or ThreadPool counts.

Files beside `<output-file>`:

- `.started.txt`: compiled identity and start status, before transport setup.
- `.samples.csv`: every measured worker/RPC start, end and latency, without batch
  latency substitution; written before final settlement validation.
- `.workers.csv`: completion totals, latency/spans, failure flags and actual
  warmup count/duration by persistent worker.
- `.fairness.csv`: worker completion counts and Jain index at common 10%, 25%,
  50%, 75%, 90% and 100% wall-time boundaries, derived after measurement without a
  sampling observer on the hot path. The 100% balance is forced by fixed counts;
  earlier points and per-worker spans are the useful observations.
- `.failure.txt` / `.worker-N.failure.txt`: full exceptions, preserved on failure.

Before and after measurement, a generated unary probe observes the server session
ID. The harness requires one ready physical connection, an unchanged ID, and zero
pending calls, active calls and client streams after settlement. Success is written
only after environment disposal has completed. These checks do not replace
deterministic lifecycle/credit tests or prove that every internal byte budget is
settled; no such acceptance claim is emitted.

## Evidence scope

All modes continue to use `dev-SendPump`. D only separates send/receive controller
locks; `b3ReadyWriterEnabled=false` is explicit. These measurements are real
generated RPC evidence for a small control, not B3, a microkernel, or a full
production acceptance result. Full byte validation and this worker harness also
mean the absolute numbers must not be directly compared with historical #735,
#800 or #803 harness numbers. Within this experiment A/B must share the same
measurement hash, payload policy, defaults, runtime options, machine and process
deadline. Use interleaved AB/BA and A/A controls, preserve every failed cell and
stop when a correctness failure appears. The output always includes
`fullAcceptanceGate=false-stage1-throughput-evidence-only`.
