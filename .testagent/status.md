# 0.8.32 test status

## Evidence status

- Five P2 candidates identified against clean 0.8.31 commit `818f23e`.
- All five proven fixes are implemented against those pre-fix probes.
- Consecutive complete audit rounds without a new improvement: 0/3.

## Current gate

- Research, test mapping, implementation, and assertion/pseudo-mutation review are complete.
- Complete pre-fix Unit: 474 total, all 470 existing tests passed and only the four new Unit probes failed. Immediate admission allocated 568 B/call after warm-up.
- Complete pre-fix Integration: 238 total, all 237 existing tests passed and only the undefined-authentication-code probe failed.
- The compression output-bound hypothesis was explicitly rejected after its proof hit the existing exact packet-writer lease before accessing excess memory; it is not counted or changed.
- Assertion review verifies replacement file existence/content, frozen old-profile lookup plus rejection of the mutated profile, stable remote authentication code, an actually sent request carrying a deadline, and measured admission bytes per call. The corresponding pseudo-mutations fail each probe.
- A pooled admission candidate measured 93.996 ns / 232 B and was rejected for a roughly 60.7% latency regression. The final exact-slot/single-lease path measures 49.262 ns / 288 B versus the 58.477 ns / 568 B baseline.
- The exact final source tree passed a non-incremental Release build with 0 warnings/errors, Generator 102/102, Unit 474/474, and Integration 238/238.
- Seven 0.8.32 packages were created; `SharpLink.Sdk` contains the Generator analyzer, and a second unique fresh-cache PackageSmoke resolved only 0.8.32 SharpLink dependencies and passed.
- Chinese/English audit, migration, performance, changelog, README, architecture, and version updates are complete. The local 0.8.32 commit is ready.
