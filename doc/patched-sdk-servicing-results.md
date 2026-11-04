# Patched SDK servicing validation result

Run [41935832233](https://github.com/SunSi12138/SharpLink/actions/runs/41935832233), attempt 1, completed successfully. Exact tools commit: `25cc40a208f8ce655af5dec0f2f00efafb09e136`. Source under test: `ba91b050cb7ea0c52c808f2e905133b184649d3b`, the main-based SDK security candidate with VersionPrefix 2.0.4. This is not the 3.0 integration candidate.

On one Ubuntu 24.04 x64 hosted runner, baseline SDK 10.0.114 and latest stable SDK 10.0.403 both executed CoreCLR runtime 10.0.14. The latest SDK was resolved at run time, not assumed to be a different runtime. All four producer/consumer edges passed all 58 fixtures, for 232 results with zero blockers. Every result was classified `IDENTICAL_BYTES_AND_COMPATIBLE`; none required the byte-different-but-compatible classification. The unchanged semantic, segmented-decode, wire-hash, fixture-policy and exact-identity checks passed.

All 16 Python orchestration controls passed in CI. Source and tool custody checks passed, as did post-execution source rechecks. The run has zero uploaded artifacts. Raw process logs, corpora and detailed reports were not uploaded; only bounded identity, counts and classifications were retained. This summary is not a fresh independent reanalysis of unavailable raw corpora.

## Limits

- This establishes the four current-baseline SDK servicing edges on this source and Linux x64 host. It is not a historical 10.0.102/runtime 10.0.2 bridge, since both lanes used runtime 10.0.14.
- It does not add cross-platform, mobile/WebAssembly, Trim/NativeAOT, whole-product regression, performance or deployment evidence.
- D2, cancellation regressions, 3.0 full-suite and performance acceptance remain separate obligations.
- No production source or internal-access grant changed. No PR merge, package release or deployment occurred.

Machine-readable bounded evidence: [patched-sdk-servicing-results.json](patched-sdk-servicing-results.json). [Validation design](patched-sdk-servicing-validation.md).
