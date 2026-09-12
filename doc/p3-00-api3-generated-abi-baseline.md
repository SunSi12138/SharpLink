# P3-00 generated API 3 performance baseline

This baseline captures the API 3 direct Runtime path before the atomic generated API 4 cut.
P3-01 and P3-GATE must use the same runner and settings so that API 4 is compared against this
exact workload rather than against the older integer-only streaming benchmarks.

## Environment and method

- Source commit: `6dab6a4830366b9df981b11f428f362415ab1ad8`
- Host: Ubuntu bare metal (`SunSiUbuntu`), Ubuntu 26.04 x64, .NET 10.0.10
- CPU affinity: logical CPUs `4-7`
- Runs: seven per scenario, odd runs in forward order and even runs in reverse order
- Warmup: 100 full-stream operations; Unary uses 500 operations
- Measurement: three seconds per run, with at most 200,000 operations
- Statistics below: median of seven independent processes
- Validation failures: zero across 49 streaming and seven Unary runs

Each streaming operation completes one full RPC call. Producers reuse their source payload to
exclude setup noise, while serialization, transport, deserialization, terminal handling, and
stream disposal remain measured. A deterministic score validates item count, payload size, and
first/last-byte sentinels on every operation.

## Unary baseline

| Scenario | Runs | Ops/s | P50 us | P99 us | CPU us/op | Allocated B/op |
|---|---:|---:|---:|---:|---:|---:|
| Server StaticDefault | 7 | 59,141.1 | 14.969 | 26.890 | 65.171 | 952.138 |

Unary allocation ranged from 951.988 to 952.195 B/op across the seven process-wide measurements.
The API 4 cut must not increase deterministic Unary allocation or introduce a statistically stable
throughput, P50, or P99 regression.

## Streaming baseline

| Scenario | Runs | Items | Item bytes | Ops/s | Items/s | P50 us | P99 us | CPU us/op | Alloc B/op | Alloc B/item |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Client100x16 | 7 | 100 | 16 | 13,285.5 | 1,328,551.3 | 62.00 | 226.66 | 294.08 | 12,795 | 128.0 |
| Client100x4096 | 7 | 100 | 4,096 | 1,716.0 | 171,601.6 | 503.52 | 887.62 | 2,192.48 | 434,126 | 4,341.3 |
| Duplex100x16 | 7 | 100 | 16 | 9,026.2 | 902,619.9 | 82.60 | 388.31 | 416.99 | 19,044 | 190.4 |
| Duplex100x4096 | 7 | 100 | 4,096 | 1,182.6 | 118,255.8 | 812.97 | 1,475.56 | 2,887.53 | 850,654 | 8,506.5 |
| Server100x16 | 7 | 100 | 16 | 14,066.2 | 1,406,616.9 | 61.07 | 200.43 | 277.67 | 15,269 | 152.7 |
| Server100x4096 | 7 | 100 | 4,096 | 1,613.3 | 161,325.1 | 548.37 | 918.23 | 2,269.24 | 447,494 | 4,474.9 |
| Server1x16 | 7 | 1 | 16 | 38,252.6 | 38,252.6 | 23.33 | 48.93 | 102.38 | 2,099 | 2,098.8 |

Allocated bytes are process-wide client/server managed allocation deltas. Allocation per item is
that delta divided by all successfully completed stream items; it is not a client-only estimate.

## Reproduction and comparison contract

Run `eng/run-generated-abi-performance-evidence.sh` from a restored checkout on the same Ubuntu
host. The defaults encode the CPU affinity, seven alternating runs, warmup, duration, and complete
scenario matrix used here. Raw P3-00 evidence is retained under
`artifacts/p3-00/api3-formal-6dab6a4/` in the isolated task checkout.

For P3-GATE, API 4 must not increase steady-state allocation per item. Streaming throughput, P50,
and P99 may regress by no more than 3% in any required scenario. The 3% value is a rejection limit,
not an optimization target; no hidden API 3 execution path or runtime feature switch may be used.
