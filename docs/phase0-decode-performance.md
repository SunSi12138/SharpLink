# #273 Phase 0 decode execution evidence

This slice is benchmark-only and is stacked on the reviewed call-reservation primitive from #276. It does not wire a decode strategy into the production request loop.

## Candidate execution models

- **A — ThreadPoolHandoff**: one per-request ThreadPool handoff before synchronous provider decode. This is the #261-style scheduling baseline.
- **B — InlineProvider**: reserve, call the existing synchronous compression provider inline, then activate. The built-in Brotli provider already decodes in bounded 8 KiB output chunks and checks cancellation in its loop.
- **C — CooperativeQuantum**: benchmark-only Brotli decoder that preserves SharpLink integrity-trailer/CRC validation, decodes in the same 8 KiB chunks, and reschedules after a bounded 64 KiB output quantum.
- **D — PersistentExecutor**: bounded channel plus a fixed persistent worker set (1..4 workers based on runner CPU count), with bounded queue depth and explicit ownership transfer.

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

A separate burst probe records Stop/Drain latency for each strategy.

Capacity-full cases are executable correctness assertions: any decompression call, decoded-buffer rent, or compressed-payload retention fails the evidence run. This preserves the #244 requirement while comparing execution models.

## Interpretation boundary

This runner is intended to select the Phase 0 execution model before production plumbing. It does not establish the final `RequestPermit`, Stop/Drain integration, decode byte budgets, dynamic policy generation, or RequestLoop ownership model. Those remain production follow-up after the execution strategy is selected.
