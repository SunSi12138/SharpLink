# Flow-state Phase B research (#735 / #742)

This continues Phase A; it does not undo the No-Go for the old production
candidate, and does not close the reopened architectural investigation.

## Frozen comparison

- Phase A source: `65892a1d9cef5a79b6afb865e667935a4b5b5e33`, including atomic
  first-admission/generation capture. `prepare-flow-state-phase-b.py` checks
  exact Git blob hashes before generating any experimental controller.
- The established pre-Phase-A baseline remains
  `56c643cd308f294cb03df79d9f1214fb8292affb`. Historical A-vs-dev results must
  not be relabeled as B-vs-A results.
- Production `src/`, wire protocol, default windows, routing, and the Phase A
  implementation are unchanged by this research increment. Generated A/B0
  controllers live only in the non-packable test project's `obj/` directory.

## Experiments and what they establish

**B0** is the complete Phase A controller with receive state, receive connection
credit, consumed batching, and receive pooling moved to a separate gate. Send
state, send connection credit, first admission, FIFO waiters and send pooling
retain the original gate. Terminal publication takes send then receive, the
only nested gate path, and completes callbacks after releasing both. The CI
validation job applies exactly this split to a disposable checkout and runs
the existing flow-controller matrix, including session first-admission tests.

A/B0 compare send acquire+unsent-return, receive accept+consume, and concurrent
send/receive loops. They are controlled microkernels, not TCP/SharedMemory
throughput tests. Splitting gates does **not** reduce two gate entries per pair;
it measures unnecessary cross-direction coupling. Same-direction regression
or improvement must not be hidden behind a Duplex aggregate.

**B1** is a send accounting model with one logical connection owner and a
bounded, single-reader Channel. It submits every acquisition to the owner.
**B2** uses the same model and queue but grants 256 B / 1 KiB / 4 KiB of bounded
connection credit. Local debit and receipt settlement take only a per-stream
gate and read a shared pressure flag; no connection-wide RMW is added to the
local item loop. Peer-update commands use the same every-64-items schedule in
B1 and B2. All commands, including peer updates, count toward handoffs/item.
A 4 KiB item with a grant of at most 4 KiB is an essential negative control:
it still requires a handoff on every item.

The grant model is **not a drop-in production controller**. In particular:

- It is send-only; receive thresholds remain tested on the real A/B0
  controller, not implemented by the grant model.
- Updates are generation-tagged model events, not key-only wire WindowUpdate
  messages. Real wire tombstone routing/duplicate update behavior still needs
  differential integration tests.
- It permits one unsettled receipt and one pending acquire per stream. This
  is an explicit model restriction, not proof of all production multi-handle
  interleavings or publication/abort races. Close revokes an unpublished
  model receipt; it does not prove safe retirement after a real writer owns it.
- Pending state-capacity admission and the exact production abort/error
  contract are not implemented. Adaptive grant sizing is not implemented.
- Queue commands/completions currently allocate. B/item must be reported;
  pooling or a reusable completion design is still required before a
  production no-allocation-regression claim.

The ledger invariant includes acquired but unpublished receipts:

```
free connection credit + unspent local grants
+ pending unpublished bytes + committed outstanding bytes
= negotiated connection window
```

Oversized acquisition is permitted only with the existing full-window
borrow precondition. On waiter pressure the owner first publishes a freeze
flag, takes each local gate to reclaim unspent grants, and admits only the
requested bytes in FIFO order. It skips stream-credit-blocked requests, but
not a connection-credit-blocked head. Cancellation and closed/stale waiters
are removed independently of that blocked prefix. This avoids both grant
hoarding and cancellation/close starvation behind a blocked head.

## Attribution and measurement limits

Run from the repository root with .NET 10 and Python 3:

```sh
python3 eng/test-flow-state-phase-b.py
dotnet run -c Release --project test/SharpLink.FlowStatePhaseB -- --self-test
dotnet run -c Release --project test/SharpLink.FlowStatePhaseB -- --b0-only --items 10000 --repetitions 4 --output artifacts/phase-b/b0.json
dotnet run -c Release --project test/SharpLink.FlowStatePhaseB -- --grants-only --items 1024 --repetitions 4 --output artifacts/phase-b/grants.json
python3 eng/summarize-flow-state-phase-b.py artifacts/phase-b
```

Use `--diagnose --b0-only` for separate wait/hold timestamp attribution.
Non-instrumented A/B0 counts use the audited two-entry inventory; diagnostic
runs count actual entries under the corresponding gate. Wait/hold sums across
workers are not wall-clock percentages. Timestamp/counter instrumentation can
perturb contention and is not used for performance acceptance.

Owner handoffs mean submitted owner commands, not OS thread switches. Queue
operations count enqueue plus dequeue. Explicit RMW counts cover only our
submission counters, not Channel, Lock, task or runtime internals; the latter
are explicitly `null` (unmeasured), never zero. Direct metrics are required
because process-wide `Monitor.LockContentionCount` cannot attribute cost to
one controller lock. See the official runtime metric definition:
https://learn.microsoft.com/en-us/dotnet/core/diagnostics/built-in-metrics-runtime

Fixed worker counts and alternating A/B order reduce some scheduler bias,
but these short exploratory loops are not a stable throughput threshold
claim. Grant measurements use asynchronous producers; their timings must
not be compared directly with the full-controller family. Warmup/setup,
serialization and transport are outside the measured owner-model loop.

## Acceptance boundary

B0 correctness and B2 model conservation are separate checks. A lower
B2/B1 handoff count validates the batching mechanism, not full Phase B Go.
Still required: pooled/no-extra-allocation completion paths, real per-stream
publication leases, key-only wire events, full lifecycle/FIFO differential
coverage, receive-side batching/connection-threshold liveness, waiter latency
and fairness distributions, independent instruction attribution, one-item
stream controls, and end-to-end TCP/SharedMemory A/B against the same Phase A
base. JIT PGO ON/OFF and NativeAOT research runs belong to their exact head.

Neither the old Phase A contention regression nor an incomplete B2 model is
a reason to close the reopened issue or declare the architecture No-Go.
