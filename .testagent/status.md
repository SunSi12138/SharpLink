# 0.8.0 test status

## Research → Plan → Implement

- Baseline: `v0.7.11`, commit `0151db10c89c8067859daef06ef04e2905cd0e89`.
- Pre-fix Unit evidence: 335 total, 6 failed across exact Codec consumption, canonical markers, and cross-stream connection credit.
- Pre-fix Generator evidence: 81 total, 2 failed for inherited methods and selected-Codec unmanaged requests.
- One suspected overload defect was rejected after the existing `SHARPLINK027` diagnostic reproduced the intended behavior.

## Focused behavior evidence

| Requirement | Evidence |
| --- | --- |
| Exact fixed/variable Codec consumption | `FixedLengthCodecsShouldRoundTripSingleAndMultiSegmentAndRejectTruncation`, `StringCodecShouldValidateLengthsAndDecodeAcrossSegments`, `BlitCollectionsShouldValidateLengthBeforeAllocationAndRoundTrip` |
| Canonical Boolean/nullable markers | `BooleanAndNullableCodecsShouldRejectNonCanonicalMarkers` across contiguous and segmented payloads |
| Cross-stream connection credit | `ConnectionThresholdShouldNotStrandConsumedCreditOnAnotherOpenStream` and real frame test `ConnectionThresholdShouldSendCreditForEveryContributingStream` |
| Inherited RPC methods | `RpcContractShouldGenerateInheritedBaseMethods`, including inherited-only and directly redeclared signatures |
| Selected Codec for unmanaged requests | strengthened `SelectorShouldOverrideUnmanagedNativeFallback` emitted-source assertions |

## Assertion and pseudo-mutation review

- New tests use exception, equality, collection, negative, state, and emitted-structure assertions; none is assertion-free or trivial-only.
- Contiguous and segmented branches, null/empty/positive collection shapes, valid 0/1 markers, invalid markers, truncation, and trailing bytes are distinguished.
- Pseudo-mutation review found two initially surviving changes: removing session queue draining, and failing to de-duplicate a directly redeclared inherited method. Both now have regression assertions and were fixed.

## Validation

- Release solution build: 0 warnings, 0 errors.
- Generator: 81/81 passed.
- Unit: 336/336 passed.
- Integration: 227/227 passed.
- RuntimeHotPath BenchmarkDotNet medians: 93.09%–101.64% of 0.7.11 latency; allocations unchanged for all seven cases.
- `git diff --check`: passed; final Release and all three test projects were rerun after the 0.8.0 version/documentation update.
