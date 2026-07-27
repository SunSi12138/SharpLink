# 0.8.31 test status

## Evidence status

- Five P2 candidates identified against clean 0.8.30 commit `6ecdac9`.
- Complete pre-fix Unit run: 473 total, all 468 existing tests passed, and only the five new probes failed.
- Official .NET pipe-transfer and stable cross-Unix lstat ABI evidence is recorded in research and audit documentation.
- Consecutive complete audit rounds without a new improvement: 0/3.

## Current gate

- The five proven fixes are implemented. Three obsolete-registry tests were deleted with that unsupported surface; exact-final Unit target is therefore 470.
- Assertion-quality review verifies endpoint byte identity after source mutation, replacement file existence and exact content, raw writer/token visibility, both local pipe-handle closures plus idempotence and redaction, and every removed/internalized type individually.
- Pseudo-mutation review kills returning the custom endpoint source, bypassing Unix identity or replacement preservation, exporting either raw framing type, omitting either parent handle closure or redaction, and leaving any retired/helper type public.
- Three identity-bearing raw-writer designs were rejected after measurable nanosecond-scale regression. The final writer body is restored verbatim and internalized; contemporaneous Release measurements are 3.473 ns baseline versus 3.524 ns candidate (about +1.5%, both 0 B/op, inside the 5% gate).
- The exact final source tree passed a non-incremental Release build with 0 warnings/errors, Generator 102/102, Unit 470/470, and Integration 237/237.
- Seven 0.8.31 packages were created; `SharpLink.Sdk` contains the Generator analyzer, and a fresh-cache PackageSmoke resolved only 0.8.31 SharpLink dependencies and passed.
- Chinese/English audit, migration, performance, changelog, README, architecture, and version updates are complete. The local 0.8.31 commit is ready.
