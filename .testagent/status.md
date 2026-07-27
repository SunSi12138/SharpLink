# 0.8.27 test status

## Evidence status

- Five P2 candidates identified against clean 0.8.26 commit `1d8325e`.
- Complete pre-fix Unit run: 454 total, all 449 existing tests passed, and exactly five new regression probes failed.
- Evidence captured silent `default(int)`, a 250 ms masked-cancellation wait, one detached writer after a 15 ms race probe, a 500 ms missed Host stop, and an illegal second anonymous-pipe attempt failing as `UnauthorizedAccessException` instead of the one-shot guard.

## Current gate

- All five fixes are implemented; strengthened Unit 454/454 passes.
- Assertion/pseudo-mutation review covers payload-bearing empty input, payload-less unexpected bytes, distinct dual tokens, post-race detached queue inspection, normal and unexpected hosted completion, and first/second anonymous-pipe attempts.
- A 15-sample runtime A/B shows no regression: writer rent/return 8.884 → 8.830 ns, Int32 response completion 44.174 → 43.836 ns, and stream dispatch/consume 16.795 → 16.803 ns with unchanged allocations.
- The exact final tree passed a non-incremental Release build with 0 warnings/errors, Generator 101/101, Unit 454/454, Integration 237/237, seven-package pack, SDK analyzer-content inspection, and fresh-cache package smoke resolving only 0.8.27 SharpLink packages.
- Version and Chinese/English audit, migration, performance, changelog, README, transport XML, and package documentation are complete. The local 0.8.27 commit is ready.
- Consecutive complete audit rounds without a new improvement: 0/3.
