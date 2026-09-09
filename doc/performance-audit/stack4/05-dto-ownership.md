# DateTimeOffset ImmutableArray ownership transfer

Stack continuation after #636; this layer only removes the second decoded array copy.
ReadDateTimeOffsetCollection creates and validates a fresh array, never pool-owned or caller-owned. AsImmutableArray takes that unique storage; null remains default and empty remains non-default empty. No validation or byte format is relaxed.

Previous isolated current-base measurements: 1/8/256/4096 items allocate 80/304/8240/131120 B before, 40/152/4120/65560 B after. Timing in those runs improved 12.06/2.16/2.15/7.83 percent. These are codec measurements, not RPC QPS; v1.1.1 already allocates one array and is not claimed to be 50 percent worse. UTC was used for cross-version timing because a nonzero-offset semantic discrepancy was found in the old code.

This layer retains alias and default/empty regression tests. Whole-RPC recovery is assessed separately against v1.1.1; no automatic merge or relaxed validation.
