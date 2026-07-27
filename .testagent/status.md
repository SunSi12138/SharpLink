# 0.8.30 test status

## Evidence status

- Five P2 candidates identified against clean 0.8.29 commit `88039d5`.
- Comprehensive performance-pattern scan completed and manually triaged; cold-path and extensibility false positives are recorded in research rather than changed.
- Complete pre-fix Generator run: 102 total, all 101 existing tests passed, and only the new Task/ValueTask semantic probe failed.
- Complete pre-fix Unit run: 468 total, all 464 existing tests passed, and only the four new hosting/address/allocation probes failed. Local health polling allocated exactly 9,600,000 bytes across 100,000 calls (96 B/call).
- Consecutive complete audit rounds without a new improvement: 0/3.

## Current gate

- Research, scan checklist, test mapping, and complete pre-fix evidence are complete.
- Production implementation has not changed.
- The five proven fixes are implemented; Generator is 102/102 and Unit is 468/468. The hosted lifecycle probe also checks duplicate Start without increasing the recommendation/test count.
- Assertion-quality review found no assertion-free or trivial-only probe: tests preserve the original Stop failure and Host token, verify terminal readiness/ownership and exact duplicate-Start message, assert both Proxy and Stub Task paths, cover every invalid logical-name character at both public address types, and combine health semantics with exact allocation.
- Pseudo-mutation review kills removal of the Run-fault stop guard, lifecycle gate or either Start-state check, changing the outer-type prefix back to substring matching, bypassing either address constructor or any invalid character, and restoring per-call `Task.FromResult`. No material survived mutation remains in the five branches.
- The exact final source tree passed non-incremental Release build with 0 warnings/errors, Generator 102/102, Unit 468/468, Integration 237/237, seven-package pack, SDK analyzer inspection, and fresh-cache package smoke resolving only 0.8.30 SharpLink packages.
- Final Generator A/B (median of three process medians) is 15.438 → 15.411 ms with essentially unchanged allocation. Local health polling is 96 → 0 B/call; the bimodal nanosecond distributions overlap, with a disclosed roughly 5 ns worst process-median tradeoff in this low-frequency path.
- Version, changelog, README, architecture, Chinese/English audit, migration, and performance documentation are complete. The local 0.8.30 commit is ready.
