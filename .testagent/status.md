# 0.8.28 test status

## Evidence status

- Five P2 candidates identified against clean 0.8.27 commit `656271b`.
- Complete pre-fix Unit run: 459 total, all 454 existing tests passed, and exactly five new regression probes failed.
- The first candidate set also produced three useful negative results: DNS/retry maximum jitter and near-`int.MaxValue` flow credit already behave correctly on the supported runtime and were removed from the batch.
- Consecutive complete audit rounds without a new improvement: 0/3.

## Current gate

- Research, test mapping, and pre-fix evidence are complete.
- The five proven fixes are implemented; strengthened Unit is 459/459.
- Assertion-quality review found no assertion-free or trivial-only probe: the batch combines exact exception types, accepted boundary equality, state preservation, and negative-path assertions.
- Pseudo-mutation review kills removal and `>`/`>=` boundary changes for both keep-alive fields and all rate periods, removal of each named-pipe enum guard, omission of any server flag or acceptance of server-only `FirstPipeInstance` by a client, `<`/`<=` changes in sliding segmentation, and moving/removing the error-code guard. No material survived mutation remains in the five changed branches.
- The exact final tree passed a non-incremental Release build with 0 warnings/errors, Generator 101/101, Unit 459/459, Integration 237/237, seven-package pack, SDK analyzer-content inspection, and fresh-cache package smoke resolving only 0.8.28 SharpLink packages.
- Fifteen-sample boundary A/B kept valid binary-error writing at 11.888 → 11.968 ns with unchanged 0 B/op. Configuration-only validation added 1.14/1.49 ns; alternating writer/pending/stream runtime A/B retained allocations and stayed within the 5% gate.
- Version, changelog, README, protocol, Chinese/English audit, migration, and performance documentation are complete. The local 0.8.28 commit is ready.
