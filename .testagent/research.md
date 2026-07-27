# 0.8.23 regression-test research

## Target inventory and evidence candidates

- Boolean blit collections accept non-canonical element bytes across array, List, Memory, ReadOnlyMemory, and ImmutableArray Codecs.
- Rune and decimal blit collections bypass the scalar semantic validation shared by ordinary values.
- DateOnly, DateTime, and TimeOnly blit collections can materialize invalid temporal values.
- DateTimeOffset blit collections accept invalid UTC ticks or offsets and transmit six bytes of native padding per element.
- A truncated shared-memory server response escapes Client Connect as raw `EndOfStreamException` instead of the transport's structured `Unavailable` error.

## Acceptance checklist

- Every built-in blit collection shape rejects invalid Boolean, Rune, decimal, and temporal elements as `DataLoss`.
- DateTimeOffset collection writers clear padding without mutating caller-owned values.
- Integer and other all-bit-pattern-valid blit collections retain their existing zero-allocation fast path.
- Truncated shared-memory server responses surface `Unavailable` with the original EOF retained as the inner cause.
- Changed valid collection paths show no material performance regression.

## Audit guardrails

The collection findings are a distinct runtime Codec path from 0.8.22 generated DTO fields and cover five concrete public collection types. The observed shared-memory EOF was converted into a deterministic truncated-peer probe rather than being accepted from one resource-contended flaky run.

## Regression and performance evidence

Against clean 0.8.22 commit `3a4338d`, the complete pre-fix Unit run contained 449 tests: all 445 existing tests passed and exactly four new collection probes failed. The complete pre-fix Integration run contained 237 tests: all 236 existing tests passed and exactly the deterministic truncated-response probe failed.

Final focused Unit 449/449 and Integration 237/237 pass. Performance A/B rejected two shared serialization helpers; the final ordinary int path retained about 10.1/17.0 ns and unchanged allocations. Sixteen-element Boolean and DateTimeOffset validation add about 5 ns and 23 ns respectively with unchanged allocations.
