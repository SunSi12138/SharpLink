# 0.8.28 regression-test plan

1. [x] Add `SocketKeepAliveDurationsBeyondNativeRangeShouldFailDuringConfiguration` for both duration fields.
2. [x] Add `RatePolicyDurationsBeyondThePortableTimerRangeShouldFailDuringConfiguration` covering token, fixed, and sliding windows.
3. [x] Add `NamedPipeTransportsShouldRejectUndefinedEnumsDuringConfiguration` for client/server option bits and transmission mode.
4. [x] Add `SlidingWindowShouldRejectAZeroTickSegmentDuration`.
5. [x] Add `BinaryErrorWriterShouldRejectUndefinedErrorCodes`.
6. [x] Run the complete pre-fix Unit suite and record all existing passes plus the exact new failure set.
7. [x] Implement only proven fixes, then perform assertion-quality and pseudo-mutation reviews.
8. [x] Run exact-final-tree non-incremental build, focused/full tests, package smoke, documentation, and performance gates; create the local 0.8.28 commit.
