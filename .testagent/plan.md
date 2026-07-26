# 0.8.0 regression-test plan

1. [x] Extend `CodecSafetyTests` so existing round-trip inventories also test one trailing byte in contiguous and segmented buffers; add malformed boolean/presence-marker cases.
2. [x] Add a `StreamFlowControllerTests` invariant that demonstrates cross-stream connection-credit stranding at the batching threshold, then strengthen it against the repaired update-batch API.
3. [x] Strengthen generator tests for adapter-selected unmanaged request values and inherited contract methods.
4. [x] Run the two narrow test projects before production changes to capture the expected failures.
5. [x] Implement the smallest production fixes, rerun narrow tests, review assertion quality and behavior gaps, and complete the performance gate.
