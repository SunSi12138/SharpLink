# Bounded .NET 11 RC1 trace feasibility

This is a separate **diagnostic-only** experiment. It changes no production source and does not relax the stable harness's rejection of instrumented samples. Source is exactly `eb99fe887cf2129d9b88441245ca0a4a6406b6c2`.

## Fixed plan

Run TCP Add c1, then SHM Add c1. For each cell run these ten fresh workload processes, in order:

1. A untraced
2. B untraced
3. B traced
4. A traced
5. A traced
6. B traced
7. B untraced
8. A untraced
9. A untraced, same-binary A/A control
10. A untraced, same-binary A/A control

There are exactly 20 workload processes, eight captures, 131072 measured operations and 32768 warmup operations per process. Metadata/build/tool probes are not workload processes. There are no additional workload cells, automatic reruns, outlier deletion, AOT experiments, or stability-acceptance gates. Two observations per traced arm establish only diagnostic feasibility.

A is traditional lowering; B toggles only the existing allowlisted production projects and generated Fixture to runtime-async. `project.py` imports the reviewed `net11/run_pilot.py` projection. The original source, compiler commands, SDK/compiler/runtime/reference-pack binaries, package graphs, generator bytes, generated source, assembly identities, method signatures and actual lowering metadata are retained and gated. The exact RC1 behavior of the pooled reader is required: its custom-builder annotation persists while the B method runtime-async lowers. No automatic pooled-builder opt-out is inferred.

A new marker-capable traditional consumer is compiled against A. Its DLL/PDB/deps/runtimeconfig/apphost bytes are reused in B while B dependency hashes remain unchanged. The independently compiled B consumer is archived. The ordinary stable validator remains unchanged and rejects both traced and untraced samples from this diagnostic driver (`diagnostic=true`).

## Markers and measured boundaries

The isolated driver adds `SharpLink-Issue739` events:

- ID 1: `Start(sample)`
- ID 2: `Stop(sample, bytes, operations)`
- Process ID comes from the event header and must match the precise-counter sample

Both marker payload paths are warmed using a different sample name before GC and the measurement window. Provider enabled state is checked against the runner's explicit trace mode at warmup and both endpoints. The same marker-capable binary executes all controls.

The Start event precedes CPU/GC snapshots and the precise-byte baseline. Stop follows precise-byte and timing endpoints plus endpoint CPU/GC snapshots. Therefore the **marker window is wider than the precise byte/time window**. Allocation ticks can straddle either boundary. The existing sentinel, admitted-zero check and fixed 20 ms tail remain; this is not a formal dispatcher-cleanup fence or pure steady-state throughput.

## Toolchains and first-capture gate

Measured target:

- SDK `11.0.100-rc.1.26425.128`
- Runtime `11.0.0-rc.1.26425.128`, explicit `dotnet exec --fx-version`

Collector and decoder, independently manifested:

- SDK `10.0.112`
- Runtime `10.0.12`, explicit `dotnet exec --fx-version`
- `dotnet-trace` package `9.0.661903`
- TraceEvent package `3.1.30`, actual assembly version `3.1.30.0`

The collector uses a generated shell wrapper, so its old net8 runtimeconfig never relies on ambient roll-forward. The measured child remains explicitly pinned to net11. The isolated decoder imports the prior parser with a narrow additional loaded-runtime/corelib proof and a raw unrecognized-allocation-event count. It does not change event weighting or attribution semantics.

The first real capture is TCP/B/traced at position 3. It must pass conversion, exact marker sample/PID/bytes/operations, tool/runtime/corelib hashes, known allocation versions (2, 3, 4), positive 64-bit weights and populated types for **every** in-window tick, and at least one resolved allocation stack. Loss accounting must be available. Reported loss, missing stacks and missing ObjectSize remain visible; success of this gate is not a completeness claim. Missing ObjectSize never becomes an exact object estimate. Failure stops the plan without a fallback toolchain or extra capture, preserving raw and partial evidence.

The same compatibility gate runs on every later trace. No first-capture support is claimed before actual successful RC1 capture evidence exists.

## Run on an authorized capture host

Install both exact SDK versions above using the existing pinned setup-dotnet workflow action. From a directory whose global.json selects SDK10.0.112:

```sh
dotnet tool install dotnet-trace --tool-path "$PWD/trace-tools" --version 9.0.661903 --allow-roll-forward
TRACE_DLL="$(find "$PWD/trace-tools/.store/dotnet-trace/9.0.661903" -path '*/tools/net8.0/any/dotnet-trace.dll' -print)"
test -f "$TRACE_DLL"
python3 eng/validation/issue739/net11_trace/run.py \
  --trace-tool-dll "$TRACE_DLL" \
  --output artifacts/issue739/dev-net11-trace
```

`--build-only` performs the exact source/build/metadata proof without any workload or capture. Counts, cases, sequence, runtime pins and collection settings are intentionally not CLI-overridable.

This runner has a 2400-second total budget; each workload is bounded, captures use 120 seconds plus the collector's drain allowance and 256 MiB raw trace cap. Each decoded JSONL artifact is capped at 512 MiB. The collector uses `gc-verbose`, the marker provider and a 256 MiB EventPipe buffer. No symbols are downloaded. Derived ETLX files and build workspaces stay under `work/`; exclude `work/**` from artifact upload, retaining everything else.

## Evidence and interpretation

The artifact includes raw `.nettrace`, launch/collector/decode logs, capture metadata, decoded JSONL and decode metadata, per-capture compatibility reports, precise samples, conservative A/T/K summaries, fixed and independent consumer binaries, actual collector/decoder payloads, source overlays, generator/Fixture source, and full hash/provenance/lowering proof. `provenance.json` status is authoritative on failure; partially exported files are not accepted samples.

A = precise managed bytes from the traced process; T = represented allocation-tick weight; K = sampled weight matching frozen rules. Signed A−T and A−K residuals are retained without clamping or rescaling, alongside T−K unknown mass, event loss, missing type/stack/size data and unresolved frame weight. Tick type/stack describes the threshold-crossing object, not exact ownership of every byte in its sampling interval. There is no 90% owner claim or exact objects/op claim.

`owners.json` is frozen before capture. Traditional state-machine rules may classify A signatures; there is no guessed runtime-async B mapping. Concrete-object rules require compatible source frames. Actual RC1 A/B types and frames must be reviewed before owner comparisons. Offline reclassification must preserve original JSONL/summaries and write separate copied summaries with the new rules and new rule hash. Never reuse net10/main percentages or recapture to obtain favorable results.

The aggregate report first compares trace observer effects **within A** and **within B**, then separately labels A/B traced and A/B untraced comparisons. Positions 9/10 remain separate same-binary controls. Do not compare these estimates across jobs or treat traced QPS as stable benchmark acceptance.

## Tests and current verification

```sh
python3 -m unittest discover -s eng/validation/issue739/net11_trace -p 'test_*.py'
python3 -m unittest discover -s eng/validation/issue739/net11 -p 'test_pilot.py'
python3 -m unittest discover -s eng/validation/issue739/trace -p 'test_*.py'
```

At implementation time, all 47 offline tests passed. The overlaid driver and decoder also compiled with SDK10.0.102/runtime10.0.2 against available local dependencies, zero warnings/errors; the actual TraceEvent assembly version was confirmed as 3.1.30.0. This is source/API smoke coverage only. The exact SDK11 build and actual RC1 trace compatibility have not been proven locally. Local Unix diagnostic sockets were previously denied; no local captures were attempted or retried for this harness.
