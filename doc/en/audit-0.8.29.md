# SharpLink 0.8.29 deep audit

Chinese: [`../audit-0.8.29.md`](../audit-0.8.29.md)

Using 0.8.28 commit `a66eccc` as the baseline, this batch verified five P2 improvements: pending registrations could outlive the disposal scan; wall-clock heartbeat accounting could be suppressed or triggered by clock changes; pipe-backed logical names accepted path syntax; abstract Unix-domain endpoints were converted into filesystem endpoints during snapshot/cleanup; and Ready/Degraded multi-cluster state reads allocated 56 bytes each.

The fixes add pre/post disposal convergence, monotonic heartbeat accounting while preserving the public UTC diagnostic property, cross-platform logical-name validation, byte-preserving Unix endpoint snapshots with abstract-path exclusion, and allocation-free frozen-dictionary state counting. Before implementation, all 459 existing Unit tests passed and exactly five new probes failed. The strengthened suite is 464/464, a 512-iteration synchronized disposal race leaves no slot, and the external 50,000-iteration witness no longer reproduces.

One P3 cleanup does not advance the version batch: the common Server receive path already updates activity for every frame, so the duplicate Ping-branch activity write (which would now sample both clocks twice) was removed.

The final non-incremental Release build completed with zero warnings/errors; Generator 101/101, Unit 464/464, Integration 237/237, seven packages, and fresh-cache package smoke passed. See [`../performance-0.8.29.md`](../performance-0.8.29.md) and [`../migration-0.8.29.md`](../migration-0.8.29.md). Consecutive complete audit rounds without a new improvement remain 0/3.
