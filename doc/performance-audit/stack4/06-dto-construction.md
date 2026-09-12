# Reuse checked DateTimeOffset collection offset ticks

Stack continuation after ownership-transfer layer. All UTC, offset-minute and local-clock range checks remain in their previous order. Construct the validated result with new TimeSpan(offsetTicks), avoiding a repeated minutes conversion and catch-wrapped helper. The standalone scalar helper and its validation are unchanged. No native-layout writes or raw-copy bypass of validation.

Historical isolated comparison against the pre-candidate current implementation: 8/256/4096-item immutable array decode 120.12/2618.33/40792.92 ns -> 100.53/2313.54/36096.77 ns, same allocations. Combination with ownership transfer still trails v1.1.1's old bulk-copy path; not a claim of restored old codec speed. Small inputs showed JIT sensitivity; both the initial negative and extended-warmup positive result remain in the conversation evidence.

The included test compares exact values, offsets, error codes/messages and inner exceptions with the old validated algorithm over legal and illegal UTC/offset boundaries and every split position. Incremental codec validation is sufficient for candidate screening; major RPC hot-path changes receive separate v1.1.1 controls. No automatic merge.
