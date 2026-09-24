# Ready-writer publication and NativeAOT evidence boundary

This cumulative research increment is based on the existing #742 branch at
`fa6cc88fdcce44ad9869e91e238d54e3ff15b036`. It keeps that commit's dispatcher
drain fix and original regression tests unchanged. The reviewed 40-file source
tree before adding this note is `544b7ac907444f0905acb2c2f6ebc645ea96d831`.
Default shipping `src/` is identical to the parent. The SendPump integration is
applied only to a disposable, hash-checked experimental checkout.

## Historical results are not new-head results

`flow-state-phase-b-transport.md` and `flow-state-ready-writer.md` are retained
historical local experiment records. Their dates, source trees, failures and
figures describe those experiments, not the current CI result. In particular,
old missing-runtime-pack failures do not predict whether the new native job
passes. The new jobs record the checkout SHA, separately transformed measured
tree, executable digest, environment, every report and process exit.

Subsequent cumulative changes include the serialized prepared-byte cap, cleanup
that proceeds even when cancellation callbacks throw, and event-based deadline
test barriers. The prepared cap is not a full connection-memory limit: a caller
may still hold one packet outside its ring and SendPump has its own budget.
No protocol/default-window changes or enlarged pool budgets are made.

## Minimal native host

`test/SharpLink.ReadyWriterAotEvidence` links the exact ReadyWriter/transport
sources, shared report contract and grant model. It has no BenchmarkDotNet or
compiler package references. The existing `SharpLink.Benchmarks` friend assembly
name grants the same internal access as the ordinary evidence host; the project
is not packable and is not referenced by production projects.

JSON serialization uses a source-generated context. Reflection serialization is
explicitly disabled in this host. Reports record RuntimeFeature's dynamic-code
support; the native verifier rejects JIT reports even if the PGO label is changed.
A managed build of this host and its 63 self-checks are useful pre-push checks,
but they are not a NativeAOT execution result.

The native CI job must publish and execute the native program, run the same
63 checks, then complete all eight independent transport processes / 128 rows:
TCP and SharedMemory, c128, tiny and 4 KiB items, two AB/BA process launches,
four rounds per mode, q1 and q16, with the same 8 KiB prepared cap on both sides.
Its independent validator also checks every credit, buffer, event and exit
invariant inherited from the JIT budget evidence. No retry or favorable-cell
selection is allowed. It retains negative throughput and allocation deltas.
The JIT budget job remains the full 48-process / 768-row comparison, including
the former count-only and 8/16 KiB preparation controls.

## Pre-publication review

The reviewed implementation was compiled in default and disposable experimental
worktrees. Benchmark, UnitTests and minimal managed-host builds reported zero
warnings/errors. Default and experimental full UnitTests passed 1897/1897 with
exit zero; ready-writer checks passed 63/63, existing model checks 74/74. The
58 Python guards, reference boundaries and unchanged maintainability budgets
passed. Local offline dependency restore used NuGetAudit=false and is not an
online audit. One additional managed TCP report validates source-generated JSON
and all 16 mode/round rows; it is a serialization/transport smoke, not a new
performance claim. New-head CI and native execution remain required.

## Remaining acceptance

The current B3 ready scheduler uses static, non-pooled stream identities and
balanced key-only credit returns. It is not a compatible full generated RPC
replacement. Dynamic lifecycle/ABA, global FIFO semantics, logical-call
cancellation/deadline, progress under blocked transport, duplicate/excess credit
policy, full Duplex, cold/retained allocation and instruction attribution still
require explicit work and evidence. Existing Phase A/B2 tests do not transfer
those guarantees to B3. Successful evidence collection is not production Go.
Keep #735 open and #742 Draft until the actual acceptance boundaries are met.
