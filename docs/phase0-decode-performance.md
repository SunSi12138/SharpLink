# #273 Phase 0 decode execution evidence

This slice is benchmark-only and is stacked on the reviewed call-reservation primitive from #276. It does not wire a decode strategy into the production request loop.

## Candidate execution models

- **A — ThreadPoolHandoff**: one per-request ThreadPool handoff before synchronous provider decode. This is the #261-style scheduling baseline.
- **B — InlineProvider**: reserve, call the existing synchronous compression provider inline, then activate. The built-in Brotli provider already decodes in bounded 8 KiB output chunks and checks cancellation in its loop.
- **C — CooperativeQuantum**: benchmark-only Brotli decoder that preserves SharpLink integrity-trailer/CRC validation, decodes in the same 8 KiB chunks, and reschedules after a bounded 64 KiB output quantum.
- **D — PersistentExecutor**: bounded channel plus a fixed persistent worker set (1..4 workers based on runner CPU count), with explicit ownership transfer.

The C implementation is intentionally local to the benchmark project. It is not a proposed public provider API or production implementation.

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

Two independent hosted-runner workflow executions were used for the decision. Relative ratios are calculated only against B inside the same payload/compressibility shard; absolute QPS is not compared across hosted VMs.

- workflow run `32568724302`, benchmark head `10b33c25914f3d6762904b6ead7202cb72a1d781`;
- workflow run `32568891389`, benchmark-equivalent head `0744415a4ab2abf04a8b1ea04d34f3a64df583c6`.

Both runs used 4 logical CPUs, but GitHub assigned different AMD EPYC models across shards/runs. The strategy ordering below remained stable despite that reassignment.

## Evidence collected

Per matrix case:

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
- remote cancellation observation probe when applicable.

A separate burst probe records synthetic drain-completion latency for each strategy. It is useful for relative executor supervision cost but is not a substitute for the production Stop/Drain integration suite.

Capacity-full cases are executable correctness assertions: any decompression call, decoded-buffer rent, or compressed-payload retention fails the evidence run. Across both independent runs, all 2,592 capacity-full matrix rows passed, covering 4,294,656 rejected requests with:

- accepted requests: `0`;
- decompression calls / rejected request: `0`;
- decoded bytes rented / rejected request: `0`;
- peak retained compressed bytes: `0`;
- peak decoded bytes: `0`.

This preserves the #244 requirement while comparing execution models.

## Results

B (`InlineProvider`) is the within-shard baseline (`1.000`). The ranges below are the two independent workflow medians.

| Payload / compressibility | A QPS / CPU | C QPS / CPU | D QPS / CPU | Interpretation |
| --- | --- | --- | --- | --- |
| 1 KiB / high | `0.772–0.773` / `1.300–1.407` | `0.993–1.009` / `0.997–1.003` | `0.645–0.690` / `1.547–1.556` | scheduling dominates; B/C are effectively equivalent |
| 1 KiB / low | `0.666–0.789` / `1.257–1.560` | `0.981–1.003` / `0.991–1.016` | `0.589–0.700` / `1.418–1.759` | per-request handoff/executor is too expensive |
| 64 KiB / high | `0.974–0.979` / `1.022–1.023` | `1.000–1.000` / `0.999–0.999` | `0.976–0.978` / `1.024–1.024` | 64 KiB quantum does not materially yield; B/C remain best |
| 64 KiB / low | `0.952–0.964` / `1.039–1.050` | `0.984–0.986` / `1.016–1.019` | `0.945–0.954` / `1.049–1.062` | B remains the cheapest execution shape |
| 1 MiB / high | `0.972–0.976` / `1.034–1.036` | `0.914–0.938` / `1.120–1.149` | `0.971–0.974` / `1.035–1.043` | D reaches A-like throughput/CPU without per-request ThreadPool ownership |
| 1 MiB / low | `0.953–0.963` / `1.044–1.051` | `0.939–0.948` / `1.068–1.071` | `0.967–0.974` / `1.044–1.044` | D is the best bounded-offload candidate; C pays repeated-yield cost |

P99 follows the same small-payload conclusion: A/D add substantial scheduler tails at 1 KiB, while C is essentially B until the quantum is crossed. At 1 MiB and high offered concurrency, A/D queueing can create large request-latency tails. That is not an argument for an unbounded inline reader loop; it is evidence that production D must combine bounded worker concurrency with explicit queue/retained/decoded resource budgets and admission/backpressure.

The cancellation probe directly cancels the decode token after decode begins. It verifies provider/executor token observation, but it does **not** model the key network property that an inline RequestLoop cannot consume a later remote Cancel frame while it is synchronously decoding. Therefore B's best CPU/QPS result cannot by itself justify using B for arbitrarily expensive remote-cancellable decode.

For 1 MiB probes, cancellation was observed in essentially every case in both runs, and median observation time was similar between B/A/C/D for the same compressibility. This means D does not introduce a material cancellation-token reaction penalty once work has begun; its main cost is queue/scheduler latency.

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

**Decision: select an adaptive B + D production model.**

1. **Use B / inline provider decode for the cheap path.**
   - Non-remote-cancellable accepted requests should decode inline after all required permits are held.
   - Remote-cancellable requests whose validated estimated decode cost is within the inline budget should also decode inline.
   - The Phase 0 evidence supports **64 KiB of declared/original output as the initial inline-budget candidate**, because B/C remain effectively equivalent through that point while A/D pay unnecessary scheduling cost.

2. **Use D / persistent bounded DecodeExecutor for expensive remote-cancellable decode.**
   - Above the inline budget, keep the reader/control-plane path free to process Cancel/deadline/close/Stop while decode is supervised by a small persistent worker set.
   - The 1 MiB evidence shows D at roughly `0.967–0.974x` QPS and `1.035–1.044x` CPU versus inline in the tested shards, materially better than C's repeated-yield cost while avoiding A's per-request ThreadPool scheduling model.
   - The exact production threshold should remain an internal policy input and can be tuned with later end-to-end evidence; Phase 0 selects the execution **shape**, not a new public configuration API.

3. **Do not productionize A.**
   - A remains the #261 comparison baseline.
   - At large payloads it can approach D's throughput, but it provides no durable bounded/fair executor ownership model and is especially expensive for small payloads.

4. **Do not productionize C as a separate execution model.**
   - Up to 64 KiB, C mostly behaves like B because the quantum is not crossed.
   - At 1 MiB, repeated cooperative yields consistently cost more CPU/QPS than D.
   - Keeping a second provider-specific decode state machine would add ownership and maintenance complexity without winning the measured large-payload tradeoff.

## Production follow-up implied by this ADR

The next production slice should implement only the selected adaptive model, not all Phase 0 prototypes:

`Request/frame -> cheap validation -> optional AdmissionProgram -> ResourceGovernor/RequestPermit -> CallReservation -> (inline B | bounded D) -> ActivateCall -> invoke -> exactly-once release`

Required gates before calling that slice complete:

- compression safety is always-on and independent of `_admissionController != null`;
- capacity/policy rejected compressed requests keep `Decompress=0` and decoded rent `=0`;
- D is supervised, bounded, fair across connections, and has no detached per-request workers;
- retained/decoded byte budgets are enforced before retention/rent;
- remote Cancel/deadline/close/Stop are exercised during executor decode;
- generation capture for #262/#264 remains stable across awaits and does not reset ResourceGovernor state;
- uncompressed/default fast path is re-measured after production wiring;
- final end-to-end performance gate re-runs the relevant payload/concurrency matrix against the selected production implementation.

## Interpretation boundary

This evidence selects the execution model before production plumbing. It does not establish the final `RequestPermit`, Stop/Drain implementation, decode byte-budget values, dynamic policy generation, fairness algorithm, or public configuration surface. Those remain production work under #273, with the selected adaptive B + D model as the constraint.
