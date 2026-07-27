# 0.8.29 test status

## Evidence status

- Five P2 candidates identified against clean 0.8.28 commit `a66eccc`.
- External pre-test witnesses: pending-table disposal race stranded an incomplete request on iteration 1; abstract UDS snapshot changed NUL-prefixed serialized bytes into `@`-prefixed bytes; multi-cluster state reads allocated exactly 56 B/read.
- Complete pre-fix Unit run: 464 total, all 459 existing tests passed, and exactly five new regression probes failed. The managed allocation probe measured 5,600,024 bytes for 100,000 reads (56 B/read plus measurement overhead); the heartbeat remained Ready beyond the three-second test bound.
- Consecutive complete audit rounds without a new improvement: 0/3.

## Current gate

- Research, test mapping, and complete pre-fix evidence are complete.
- Production implementation has not changed.
- The five proven fixes are implemented and Unit is 464/464. The pending-table test includes 512 synchronized races; the external 50,000-iteration probe has no witness.
- Assertion-quality review found no assertion-free or trivial-only probe: the batch checks exact exception types, terminal operation/slot state, heartbeat closure, serialized endpoint bytes and ownership, Ready semantics, and exact thread allocation.
- Pseudo-mutation review kills removal of either stream disposal precheck, the post-insert convergence read, any one of the three invalid-name character checks, monotonic timeout use, byte-preserving endpoint creation, abstract filesystem exclusion, or the allocation-free state loop. No material survived mutation remains in the five changed branches.
- The exact final tree passed a non-incremental Release build with 0 warnings/errors, Generator 101/101, Unit 464/464, Integration 237/237, seven-package pack, SDK analyzer-content inspection, and fresh-cache package smoke resolving only 0.8.29 SharpLink packages.
- Alternating 15-sample A/B kept pending response completion at 37.176 → 37.127 ns with unchanged 24 B/op, improved multi-cluster state from 8.972 ns / 56 B to 3.189 ns / 0 B, and measured the monotonic activity update/check correctness cost at +4.039 ns with 0 B/op.
- Version, changelog, README, architecture, Chinese/English audit, migration, and performance documentation are complete. The local 0.8.29 commit is ready.
