# #273 Phase 0 decode execution evidence

This slice is benchmark-only and is stacked on the reviewed call-reservation primitive from #276. It does not wire a decode strategy into the production request loop.

## Candidate execution models

- **A — ThreadPoolHandoff**: one per-request ThreadPool handoff before synchronous provider decode. This is the #261-style scheduling baseline.
- **B — InlineProvider**: reserve, call the existing synchronous compression provider inline, then activate. The built-in Brotli provider already decodes in bounded 8 KiB output chunks and checks cancellation in its decode loop.
- **C — CooperativeQuantum**: benchmark-only Brotli-loop prototype that preserves SharpLink integrity-trailer/CRC validation, decodes in the same 8 KiB chunks, and reschedules after a bounded 64 KiB output quantum. The integrity CRC is still a whole-input synchronous scan before the first cancellation check/yield, so C is **not** an end-to-end bounded cooperative decode pipeline.
- **D — PersistentExecutor**: persistent fixed workers with explicit queued-work ownership and queue-owned cancellation. The comparative A/B/C/D matrix measures this executor with an unsaturated queue; separate fixed-capacity probes exercise bounded-channel backpressure and cancellation while work is still queue-owned.

The C implementation is intentionally local to the benchmark project. It is not a proposed public provider API or production implementation, and its results apply only to this Brotli-loop `Task.Yield` shape rather than cooperative decode in general.

## Matrix

Each payload/compressibility shard runs all four strategies across:

- payload: 1 KiB / 64 KiB / 1 MiB;
- compression ratio proxy: high-compressibility / low-compressibility deterministic payloads;
- remote-cancellable token: off / on;
- call capacity: available / full;
- admission shape: off / immediate cheap policy / queued continuation;
- concurrency: 1 / 16 / 128;
- repetitions: 3, with alternating strategy order to reduce systematic drift.

The queued-admission shape is deliberately one scheduler continuation, not a production `AdmissionProgram` implementation. It isolates how an already-asynchronous admission continuation interacts with the decode execution model without prematurely coupling the benchmark to #264 production wiring.

Two independent hosted-runner workflow executions were run **after** D gained its cancellation-aware `PersistentDecodeWorkItem` and `CancellationToken.Register` hot path. Relative ratios are calculated only against B inside the same payload/compressibility shard; absolute QPS is not compared across hosted VMs.

- workflow run `32580143013`, benchmark head `b19eaec9657735ad42d769e0571cbe4e11e84a97`;
- workflow run `32580252570`, benchmark-equivalent head `2e7a56049ccd069f7cd8f2b9f1fde81f5e2bb5ea` (documentation-only change after the first run).

Earlier pre-cancellation-safe-D runs are historical evidence only and are no longer used as quantitative support for D. The ranges below come exclusively from these two current-D executions.

## Evidence collected

Per comparative matrix case:

- QPS;
- process CPU ns/op;
- request P50/P99;
- process allocated bytes/op;
- decompression calls per rejected request;
- decoded bytes rented per rejected request;
- peak retained compressed bytes in flight;
- peak decoded bytes in flight;
- peak explicit decode queue depth;
- scheduler/worker delay P50/P99;
- local cancellation-token observation probe when applicable.

A separate burst probe records synthetic drain-completion latency for each strategy. It is useful for relative executor supervision cost but is not a substitute for the production Stop/Drain integration suite.

Capacity-full cases are executable correctness assertions: any decompression call, decoded-buffer rent, or compressed-payload retention fails the evidence run. Across the two refreshed runs, all 2,592 capacity-full matrix rows passed, covering 4,294,656 rejected requests with:

- accepted requests: `0`;
- decompression calls / rejected request: `0`;
- decoded bytes rented / rejected request: `0`;
- peak retained compressed bytes: `0`;
- peak decoded bytes: `0`.

This preserves the #244 requirement while comparing the current execution models.

## Results

B (`InlineProvider`) is the within-shard baseline (`1.000`). The ranges below are the two independent **cancellation-safe-D** workflow medians. They include D's per-request work-item allocation, queued-cancellation registration, and ownership transition overhead. D is still measured with an unsaturated comparison queue; saturation/backpressure is validated separately.

| Payload / compressibility | A QPS / CPU | C QPS / CPU | D QPS / CPU | Interpretation |
| --- | --- | --- | --- | --- |
| 1 KiB / high | `0.746–0.760` / `1.313–1.341` | `0.998–1.011` / `0.988–1.002` | `0.681–0.707` / `1.383–1.505` | scheduling and D cancellation ownership dominate; B/C are effectively equivalent |
| 1 KiB / low | `0.758–0.788` / `1.266–1.304` | `0.993–0.994` / `1.006–1.010` | `0.690–0.742` / `1.393–1.499` | B remains decisively cheaper than either offload shape |
| 64 KiB / high | `0.943–0.965` / `1.036–1.060` | `0.999–1.001` / `1.000–1.002` | `0.922–0.931` / `1.061–1.074` | B/C remain best; current D's cancellation-safe fixed-worker overhead is measurable |
| 64 KiB / low | `0.944–0.947` / `1.056–1.057` | `0.986–0.989` / `1.012–1.016` | `0.916–0.942` / `1.073–1.099` | B remains the cheapest measured execution shape |
| 1 MiB / high | `0.981–0.985` / `1.032–1.033` | `0.935–0.943` / `1.112–1.135` | `0.974–0.976` / `1.034–1.043` | current D retains A-like fixed-worker throughput/CPU; this C prototype pays repeated Brotli-loop yields |
| 1 MiB / low | `0.946–0.967` / `1.043–1.061` | `0.932–0.941` / `1.064–1.078` | `0.965–0.974` / `1.040–1.051` | current D remains the best measured fixed-worker offload candidate; this C prototype pays repeated-yield cost |

The refreshed data strengthens the adaptive split rather than weakening it: D's queue-owned cancellation machinery has a visible fixed cost at 1 KiB and 64 KiB, while at 1 MiB its QPS/CPU remains close to A and ahead of this C prototype. That supports B for cheap work and D only once preserving reader/control-plane availability justifies the fixed-worker ownership cost.

P99 follows the same small-payload conclusion: A/D add scheduler tails at 1 KiB, while C is essentially B until the output quantum is crossed. At 1 MiB and high offered concurrency, A/D queueing can create large request-latency tails. That is not an argument for an unbounded inline reader loop; it is evidence that production D must combine bounded worker concurrency with explicit queue/retained/decoded resource budgets and admission/backpressure.

The cancellation probe directly cancels the decode token after decode begins. It verifies provider/executor token observation, but it does **not** model the key network property that an inline RequestLoop cannot consume a later remote Cancel/close/Stop frame while it is synchronously decoding. It therefore cannot establish a safe remote-cancellable inline threshold or bound reader-loop/control-plane stall.

For 1 MiB probes, cancellation was observed in essentially every case in both refreshed runs, and median local token-observation time remained similar between B/A/C/D for the same compressibility. This means D does not introduce a material cancellation-token reaction penalty once work has begun; it does not prove anything about how quickly a remote control frame is read when B is running inline.

C's cancellation/yield evidence also has a specific boundary: `Crc32Accumulator.Compute` scans the complete compressed payload synchronously before the Brotli loop starts. For low-compressibility 1 MiB inputs that can mean nearly the whole compressed input is traversed before C reaches its first cancellation check or output-quantum yield. The measurements therefore compare B against a **Brotli-loop-only cooperative prototype**; they do not establish the cost or viability of a design that also makes integrity validation cooperative.

### Fixed-capacity executor saturation and actual-D queued-cancellation probes

A separate saturation probe fixes queue capacity independently of offered concurrency (`queue capacity = 8`, `concurrency = 128`, `operations = 256`). Its minimal local channel harness deliberately holds workers until bounded-channel backpressure is observed, and it fails if no blocked writer is recorded or if submitted decode work does not complete after release. That harness exists only to measure channel saturation; it no longer carries a second queued-cancellation state machine.

Queued cancellation is exercised through the **same `DecodeCaseRuntime -> PersistentDecodeExecutor -> PersistentDecodeWorkItem` path used by comparative D**. A deterministic worker gate holds actual D work in queue ownership. Before cancellation the probe requires all 8 real call reservations, retained-compressed leases, decoded-output leases, and D queue entries to be in flight while `DecompressCalls=0`.

After cancellation completes but before worker release, the probe requires the real call reservations, retained-compressed bytes, and decoded-output bytes all to be released (`0`) while the 8 cancelled work items remain in the gated D queue and `DecompressCalls=0`. Across all six payload/compressibility shards in both refreshed runs, every queued-cancellation probe reported:

- `cancelled=8`;
- `providerStarts=0`;
- `skippedBeforeProvider=8` after drain;
- `reservationReleased=True` before worker service;
- `retainedLeaseReleased=True` before worker service;
- `decodedLeaseReleased=True` before worker service.

After worker release/drain, the actual D work items must all take the `CancelledBeforeStart` skip path: queue depth reaches `0`, skipped-cancel count equals 8, provider/decompress count remains `0`, and no request ownership is reacquired or leaked. The work item checks its ownership state before reading the retained/output fields, so the deterministic probe exercises the exact ordering on which safe early return of those pooled buffers depends.

This is the required semantic shape for production D: cancellation may complete caller ownership early only if cancellation wins while the item is still queue-owned. If a worker has already won ownership, the caller must continue to await that worker so retained/decoded buffers cannot be returned while provider code may still access them.

### Resource-budget observation

The benchmark intentionally records resource amplification before the production ResourceGovernor byte budgets exist. At concurrency 128 with low-compressibility 1 MiB payloads, deferred strategies can accumulate large retained/decoded in-flight totals. This is a useful negative result: the production executor must **not** simply copy the benchmark queue/rent sequence.

Production D must acquire or account for, in the RequestPermit/ResourceGovernor ownership model:

1. call reservation;
2. bounded decode queue/concurrency credit;
3. retained compressed-byte budget before long-lived retention;
4. decoded-byte budget before the large decoded rent;
5. exactly-once transfer/release across queue, worker, activation, failure, cancellation, and Stop/Drain.

The executor queue must be fixed/bounded independently of offered request concurrency, and production scheduling must add the per-connection fairness / anti-monopoly behavior required by #273.

## ADR — selected Phase 0 execution model

**Decision: select an adaptive B + D production model, with the inline threshold left unresolved until production RequestLoop control-plane evidence exists.**

1. **Use B / inline provider decode for the cheap path.**
   - Non-remote-cancellable accepted requests should decode inline after all required permits are held.
   - Remote-cancellable requests may decode inline only when a production RequestLoop experiment shows that the chosen cost budget keeps remote Cancel/close/Stop observation within an explicit control-plane stall budget.
   - **64 KiB declared/original output is only the first threshold hypothesis to test**, because B/C have similar CPU/QPS through that size while cancellation-safe D has visible fixed-worker ownership cost. Phase 0 does not establish 64 KiB as a safe remote-cancellable inline budget.

2. **Use D / persistent bounded DecodeExecutor for expensive remote-cancellable decode.**
   - Keep the reader/control-plane path free to process Cancel/deadline/close/Stop while decode is supervised by a small persistent worker set.
   - The refreshed 1 MiB evidence includes D's cancellation-aware work-item/registration cost and still shows fixed-worker D avoiding the repeated Brotli-loop-yield cost paid by this C prototype. The separate saturation probe validates bounded-channel backpressure, and the actual-D queued-cancellation probe validates cancellation before provider start together with real reservation/pooled-lease release ordering.
   - The exact production threshold remains an internal policy decision that must be validated end-to-end; Phase 0 selects the execution **shape**, not a threshold value or a new public configuration API.

3. **Do not productionize A.**
   - A remains the #261 comparison baseline.
   - At large payloads it can approach D's unsaturated fixed-worker throughput, but it provides no durable bounded/fair executor ownership model and is especially expensive for small payloads.

4. **Do not productionize this C prototype as a separate execution model.**
   - Up to 64 KiB output, it mostly behaves like B because the Brotli output quantum is not crossed.
   - At 1 MiB, its repeated Brotli-loop yields cost more CPU/QPS than cancellation-safe D in both refreshed runs.
   - Its synchronous whole-input CRC means Phase 0 has **not** evaluated a fully cooperative integrity+decode pipeline. The data therefore does not rule out such a design in general; it only shows that carrying this provider-specific Brotli-loop `Task.Yield` prototype alongside B + D is not justified by the measured tradeoff.

## Production follow-up implied by this ADR

The next production slice should implement only the selected adaptive model, not all Phase 0 prototypes:

`Request/frame -> cheap validation -> optional AdmissionProgram -> ResourceGovernor/RequestPermit -> CallReservation -> (inline B | bounded D) -> ActivateCall -> invoke -> exactly-once release`

Required gates before calling that slice complete:

- compression safety is always-on and independent of `_admissionController != null`;
- capacity/policy rejected compressed requests keep `Decompress=0` and decoded rent `=0`;
- D is supervised, bounded, fair across connections, and has no detached per-request workers;
- queued D cancellation before worker start skips provider/CRC work and releases caller ownership without waiting for worker service;
- cancellation racing queue-to-worker ownership performs a pre-provider token check, while worker-owned work prevents early buffer return;
- retained/decoded byte budgets are enforced before retention/rent;
- remote Cancel/deadline/close/Stop are exercised during executor decode;
- a real RequestLoop remote-control-frame probe measures Cancel/close/Stop observation while testing any proposed inline threshold, starting with the 64 KiB hypothesis;
- generation capture for #262/#264 remains stable across awaits and does not reset ResourceGovernor state;
- uncompressed/default fast path is re-measured after production wiring;
- final end-to-end performance gate re-runs the relevant payload/concurrency matrix against the selected production implementation.

## Interpretation boundary

This evidence selects the execution shape before production plumbing. It does not establish the final `RequestPermit`, Stop/Drain implementation, decode byte-budget values, remote-cancellable inline threshold, dynamic policy generation, fairness algorithm, or public configuration surface. It also does not benchmark a fully cooperative integrity+decode implementation. Those remain production/research work under #273, with adaptive B + D as the selected shape.
