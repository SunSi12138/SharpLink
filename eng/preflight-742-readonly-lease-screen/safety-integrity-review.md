# R preflight run 37855801421: independent integrity and safety review

**PASS for complete safety gates, retained artifact integrity and collection provenance.** The run completed successfully on attempt 1. Semantic instruction-level copy removal remains a separate review; this report establishes no speed, timing-screen acceptance, readiness or promotion result.

Run: https://github.com/SunSi12138/SharpLink/actions/runs/37855801421

- Helper commit: `7b84cd6a60dc9f8d8e7dfa8e29dc82c4adc2d535`
- Helper tree: `a5fcfa74feb6b6b6418af97da84cc93f71f0703d`
- G2 production: `398d484fb5b8ab8adb75a74db7d577d929d2dc77`
- R production: `df4383c7d00ee4a31c129ae42958e013f4ff5385`
- G2 tests: `6d96ee8d21339bdef6e3d492f854afcbef02b329`
- R tests/experimental base: `54a4426f42e8edba19e98457d7a8a1477fcfe940`

## Official artifact integrity

All 17 official artifacts were downloaded through the GitHub artifact connector and its returned Sediment file IDs. No direct download URL, oversized workaround or alternate run was used.

For every original ZIP this reviewer independently verified official artifact ID, run/head association, length, GitHub SHA-256 digest, ZIP CRC and safe unique member paths. All 501 extracted files were hashed. Every one of the 16 non-safety package maps exactly matches the manifest retained in the safety package; the safety package itself is anchored by its official original ZIP SHA-256 and CRC. No packaging failure was recorded.

All packages are strictly below 24 MiB. The largest extracted package is native-G2-code, 16,332,865 bytes. Original ZIPs, exact immutable IDs/digests, local paths and per-file verified maps are retained in `run-37855801421-metadata.json`, `run-37855801421-downloads.json` and `run-37855801421-package-integrity.json`.

All 21 helper hashes match the independently reviewed frozen bundle. Both production patches, the shared test patch, all four complete source-hash manifests, the full reconstructed G2 patch, source/test blob maps, gate hashes and original workload identities match the reviewed inputs. Failed A/V source changes are absent.

## Complete safety results

Independently checked the raw final test summaries, not only workflow status:

- G2 default: 2,142 / 2,142 passed; zero failures or skips
- R default: 2,142 / 2,142 passed; zero failures or skips
- R experimental: 2,142 / 2,142 passed; zero failures or skips
- Focused ownership controls: 14 / 14 on each default arm, with exact populations 3/4/4/2/1
- Original experimental writer checks: 171 / 171
- Original Python writer/source/collection controls: 17, 12, 8 and 13 tests, all OK
- C# builds: zero warnings and zero errors in retained safety build logs
- Whitespace format verification: passed; its log is empty on success
- Project-reference boundaries: passed, 9 projects and 13 declarations/active references
- Maintainability: passed, 1,276 files checked and the same 19 historical oversized-file allowances

Some writer parser-fixture logs intentionally contain simulated FAIL, timeout and rejected-native-population messages. Those are expected negative controls in the passing Python fixture suites. They are not real workload failures or NativeAOT execution results.

The experimental tracked source patch was independently applied only to temporary source copies and compared with the original unchanged ready-writer transformation. It matches exactly. The added writer source hash matches the original template. No pristine production root is reused from that experiment.

Safety DLLs were intentionally not archived. Their recorded produced-binary hashes and copied lifecycle/probe identities are mutually consistent, but this review does not claim independently rehashed safety binary bytes.

## Allocation and lifecycle evidence

All four original allocation cases passed on both G2/R against unchanged median and spread budgets. Every five-sample population was checked; medians, spreads and bytes-per-operation values were recomputed from the retained raw samples. The reports use Release, x64, runtime 10.0.12 and four processors, with no filter or allocation injection.

Both original warm/cold lifecycle populations contain 120 unique rows per arm. The paired comparison was independently recomputed: maximum R-minus-G2 delta is 0.0 B/request, satisfying the unchanged <=0.01 B/request gate.

The supplemental production-callback matrix likewise contains 120 matched rows per arm, with a maximum delta of 0.0 B/request. This remains the original supplemental evidence rather than a newly invented acceptance gate. Actual-session setup retains all 36 raw rows across the original F/G/G/F process order; its summary statistics and source/copied-binary provenance were independently checked.

Raw allocation values and limits are recorded in `run-37855801421-safety-integrity.json` and `run-37855801421-integrity-review.json`. No performance benefit is inferred from these safety budgets.

## JIT collection and binary evidence

Both pristine JIT output archives were independently rehashed against all 67 binary/output entries per arm, 134 total. Generated RPC source archives match their manifests. Production source hashes before build, after build, before capture and after capture agree with the frozen complete G2/R maps; captured binary maps also remain unchanged.

SDK 10.0.112/runtime 10.0.12 and original R2R=0, tiering=1, PGO=1, QuickJitForLoops=1 settings are retained. Each of the eight original diagnostic invocations has exactly one attempt, 30 warmups, five seconds, the 200,000-operation cap and four-CPU affinity. Both arms completed TCP Client100x16, SHM Client100x4096, SHM Server1x16 and SHM Server100x16.

The original frozen workload validator was independently replayed on all eight raw reports. The original frozen JIT inventory parser was also replayed on all eight raw disassembly files; all derived inventory fields agree exactly, excluding only the machine-local raw-file path. Every extracted method block matches its raw line range and SHA-256.

All eight cells' selected actual root wrapper and MoveNext bodies are **Tier1 / Synthesized PGO, with no OSR entry**. The original strict Dynamic-PGO verdict remains inconclusive in every cell. Nothing is relabeled Dynamic PGO.

Actual byte[] managed state remains 256 bytes for the client and 304 bytes for the generated server in both G2 and R, with identical fields. The owned lease remains 40 bytes. Metadata verifies that only R's synchronous unsized inner parameter is readonly-byref; the outer wrappers, sized sender and deferred helper remain by value.

These checks establish real execution, identity and retained-code coverage. They do not establish that the targeted copy vanished or that a compensating copy is absent. That remains the parent's independent instruction-level review.

## NativeAOT collection

Both original publish archives contain 15 files whose bytes match the retained publish manifests. The actual extracted ELF hashes are:

- G2: `0c21431a2db4bc7d1c0c8497c7a4124b8f594567dc3473e57211f8d4d8e01333`
- R: `3936ef2bc54768187a1667e3f18a193a270d3efd1e8814e1f81c1f67a66c0e24`

Each ELF matches the code-review record and native index. Full symbol/disassembly XZ streams were losslessly decompressed and hashed: about 20.5 MB of symbols and 151.3 MB of disassembly per arm. Source archives, generated-source maps, compiled targets including Server100, source pre/post maps and identical minimal host adapter hashes all verify. No compiler-tuning override or native collection failure is recorded.

NativeAOT scope is compiled code only. No native workload was executed or timed. ELF/code paths and immutable identities were handed to the independent native reviewer after integrity verification. Missing/inlined symbol groups are not interpreted as a mechanism pass.

## Review outputs and stopping condition

- `run-37855801421-integrity-review.json`: full machine-readable result
- `run-37855801421-safety-integrity.json`: complete raw safety/allocation verification
- `run-37855801421-package-integrity.json`: official ZIP and inner package maps
- `run-37855801421-native-byte-integrity.json`: native archive, ELF and full-stream hashes
- `run-37855801421-parser-replay.json`: independent original-validator/parser replay
- `run-37855801421-review-sha256.json`: review artifact hashes

No gate or artifact is missing, and no rerun was performed. This integrity/safety task is complete. Required next work is the separate positive caller/callee ownership and copy-removal review, including compensating-copy/spill/frame/code-size tradeoffs, before any timing screen. Performance acceptance requirements remain unchanged.
