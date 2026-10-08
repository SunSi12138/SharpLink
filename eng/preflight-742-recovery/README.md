# Read-only recovery of the original redesign evidence

Original run37757840731 completed all24 micro benchmark processes. Its helper verifier mistakenly required3 repetitions for every scenario, while the unchanged contention runner intentionally reports6 samples (AB and BA for each requested repetition). All raw files are retained.

This separate workflow downloads the exact original artifacts, validates every232-cell population with exact3/6 repetition and checksum semantics, and does not rerun any micro measurement. It also executes the previously skipped NativeAOT RPC/writer stages against the identical shipping source tree4c5943fec3a85089cefa8a1b6dc8c0f6502567ce.

The main workflow, its original triggers and all correctness/performance jobs are unchanged. Original2070/2070 default and experimental results remain separately cited. The original failed run status is not rewritten. Corrected verification and successful collection are not a Ready verdict; full production acceptance and exact PR-head CI are still required.
