# Issue #430 Zstandard feasibility and performance evidence

Status: **Go** for the algorithm-free Core boundary and the official .NET 10 Zstandard provider described here.

This document records the durable evidence used to finish the Zstandard and performance acceptance work for issue #430. Raw machine-readable rows were captured by the linked GitHub Actions runs; the benchmark/evidence runner is committed in-tree so the matrix can be reproduced, while the decision-relevant platform, percentile, CPU/allocation, wire-savings, rejection, and A/B values are preserved below.

## Candidate and profile

The production package is `SharpLink.Compression.Zstd`, targeting the repository's stable `net10.0` baseline and depending on the exact `ZstdSharp.Port` `0.8.8` package. The backend is not part of wire identity.

The official profile is:

```text
zstd-rfc8878-w23-checksum/v1
```

Its decode-relevant contract is fixed:

- exactly one standard RFC 8878 Zstandard frame;
- standard Zstandard frame checksum required;
- dictionaries forbidden;
- trailing bytes and a concatenated second frame forbidden;
- maximum window log 23 (8 MiB);
- bounded output enforced by the provider and rechecked by Core against the generic `originalLength`;
- compression level is encode-only tuning and therefore does not change `WireProfile`.

This uses the format's own checksum rather than restoring a SharpLink-specific trailer or CRC path.

## Generic SPI zero-byte representation fix

The Stage-1 SPI allowed `TryCompress(...) == true` without requiring a non-empty representation, while inbound Core validation required at least one `compressedBody` byte. That made a zero-byte provider legal on the send side but a `ProtocolViolation` on receipt.

The final contract deliberately allows a successful representation to contain zero bytes. The generic four-byte `originalLength` envelope already frames the payload, and representation validity belongs to the negotiated provider. Inbound envelope validation therefore requires the original-length prefix but does not impose an algorithm-independent body minimum. `CompressionFrameTests.ZeroByteRepresentationShouldRoundTripThroughWireEnvelope` sends and decodes a real length-only compressed frame to lock the contract end to end.

## .NET 10 backend feasibility

`ZstdSharp.Port` was selected over copying the .NET 11 implementation. The .NET 11 implementation is a managed API over `System.IO.Compression.Native`/native zstd integration; copying it to .NET 10 would amount to maintaining a Runtime/native fork. `ZstdSharp.Port` is an MIT C# port and keeps the .NET 10 package self-contained at the managed dependency level.

The provider validation covers:

- contiguous and multi-segment round trips;
- checksum corruption;
- truncation;
- trailing bytes;
- concatenated second frame;
- checksum-disabled frame rejection;
- dictionary-bearing frame rejection;
- compression output bound (`TryCompress=false`);
- decompression output bound;
- cancellation;
- concurrent calls on one provider instance;
- multiple compression levels sharing one wire identity.

Stable Stage-2 validation run: https://github.com/SunSi12138/SharpLink/actions/runs/34022883327

- Release solution build: 0 warnings / 0 errors.
- Unit: 1470 passed.
- Generator: 264 passed.
- Load-test tests: 60 passed.
- Integration: 421 passed.
- direct Zstd NativeAOT smoke: passed.
- full TCP/shared-memory NativeAOT topology, SharpPack sidecar, and pre-credit smokes: passed.
- pack, package contract verification, and package smoke: passed.

## Platform and future .NET 11 backend matrix

Platform/BCL evidence run: https://github.com/SunSi12138/SharpLink/actions/runs/34024909888

The workflow pins .NET 11 Preview 7 SDK `11.0.100-preview.7.26381.103` only as evidence. The shipping package in this change does **not** target preview `net11.0`. A stable .NET 11 TFM/backend can be added after GA without changing the SharpLink profile if these compatibility properties continue to hold.

| Platform | .NET 10 ZstdSharp NativeAOT | Separate native zstd deployment | ZstdSharp -> BCL | BCL -> ZstdSharp | corruption/truncation/trailing/concat/bound |
|---|---|---|---|---|---|
| Linux x64 | pass | absent | 12/12 pass | 12/12 pass | pass |
| Windows x64 | pass | absent | 12/12 pass | 12/12 pass | pass |
| macOS arm64 | pass | absent | 12/12 pass | 12/12 pass | pass |

Each 12-case direction covers `4 KiB / 64 KiB / 256 KiB / 1 MiB` × `DTO-like / mixed / random`. The same run also verifies ZstdSharp->ZstdSharp and BCL->BCL. Across the three platforms this is 144 cross/self-backend round trips plus the negative/bounded checks.

The byte streams are intentionally not required to be identical: for example the two encoders differ by a few bytes for several inputs. Wire compatibility is defined by mutual decoding of the fixed profile, not by backend identity or byte-for-byte encoder output.

## Direct compression/decompression

Performance/evidence run: https://github.com/SunSi12138/SharpLink/actions/runs/34024844173

Environment: `.NET 10.0.11`, Ubuntu 24.04.4 LTS, x64, 4 logical processors. The table below is the contiguous-input view; the raw evidence also contains the segmented-input rows.

| Size | Pattern | Compressed bytes | Candidate accepted | Wire savings | Compress MiB/s | Decompress MiB/s | Compress B/op | Decompress B/op |
|---:|---|---:|---|---:|---:|---:|---:|---:|
| 4 KiB | dto | 107 | yes | 97.29% | 75.4 | 160.1 | 144.0 | 136.0 |
| 4 KiB | mixed | 1,131 | yes | 72.29% | 46.0 | 366.2 | 144.0 | 136.0 |
| 4 KiB | random | 4,110 | no (raw fallback) | 0.00% | 64.4 | 383.6 | 144.0 | 136.0 |
| 64 KiB | dto | 107 | yes | 99.83% | 585.6 | 920.4 | 144.1 | 136.1 |
| 64 KiB | mixed | 16,880 | yes | 74.24% | 197.0 | 1037.4 | 144.1 | 136.1 |
| 64 KiB | random | 65,550 | no (raw fallback) | 0.00% | 646.1 | 1716.7 | 144.1 | 136.1 |
| 256 KiB | dto | 124 | yes | 99.95% | 1242.8 | 4383.0 | 144.3 | 136.3 |
| 256 KiB | mixed | 67,342 | yes | 74.31% | 261.0 | 2222.9 | 144.3 | 136.3 |
| 256 KiB | random | 262,166 | no (raw fallback) | 0.00% | 2182.7 | 8727.9 | 144.3 | 136.3 |
| 1 MiB | dto | 196 | yes | 99.98% | 2968.7 | 5224.1 | 145.2 | 137.2 |
| 1 MiB | mixed | 269,246 | yes | 74.32% | 332.2 | 6295.5 | 145.2 | 137.2 |
| 1 MiB | random | 1,048,616 | no (raw fallback) | 0.00% | 1752.1 | 7873.6 | 145.2 | 137.2 |

Random payloads correctly produce a valid Zstandard candidate but fail Core's benefit test and therefore fall back to raw; their rejection rate is 100% in RPC evidence.

For the eight accepted DTO/mixed cases, segmented-input compression throughput is `1.03x` contiguous at the median (range `0.88x–1.36x`). Segmented decompression is `0.88x` at the median (range `0.50x–1.49x`). The direct runner observed no additional per-operation allocation from switching contiguous input to segmented input for these cases. The complete per-shape numbers are retained in the JSON snapshot.

## Local end-to-end RPC evidence

The full local matrix contains TCP and SharedMemory, all four payload sizes, requested concurrency `1 / 8 / 32 / 128`, disabled compression, and all three Zstd patterns. QPS, P50/P99/P99.9, CPU/op, B/op, GC counts, working-set/managed-heap observations, wire savings, and candidate rejection rate are retained for every row in the JSON snapshot.

Requested concurrency is always recorded. `EffectiveConcurrency` is capped when the original in-flight payload bytes would consume more than 75% of the production `Balanced` 8 MiB send queue. This preserves production defaults instead of manufacturing a larger queue for the benchmark. The resulting cap is:

```text
4 KiB:   1 / 8 / 32 / 128
64 KiB:  1 / 8 / 32 / 96
256 KiB: 1 / 8 / 24 / 24
1 MiB:   1 / 6 / 6 / 6
```

Representative requested-concurrency 8/32 rows:

| Transport | Size | Requested c | Effective c | Mode | Pattern | QPS | P99 ms | CPU us/op | B/op | Wire savings | Rejection |
|---|---:|---:|---:|---|---|---:|---:|---:|---:|---:|---:|
| tcp | 4 KiB | 8 | 8 | disabled | pattern-independent | 43246 | 0.36 | 81 | 8780 | 0.00% | 0% |
| tcp | 4 KiB | 32 | 32 | disabled | pattern-independent | 59185 | 0.94 | 62 | 8807 | 0.00% | 0% |
| tcp | 64 KiB | 8 | 8 | disabled | pattern-independent | 11300 | 1.25 | 298 | 133322 | 0.00% | 0% |
| tcp | 64 KiB | 32 | 32 | disabled | pattern-independent | 12242 | 4.07 | 305 | 138274 | 0.00% | 0% |
| tcp | 256 KiB | 8 | 8 | disabled | pattern-independent | 3411 | 4.10 | 867 | 551995 | 0.00% | 0% |
| tcp | 256 KiB | 32 | 24 | disabled | pattern-independent | 3049 | 10.35 | 948 | 578618 | 0.00% | 0% |
| tcp | 1 MiB | 8 | 6 | disabled | pattern-independent | 726 | 17.91 | 3149 | 2328716 | 0.00% | 0% |
| tcp | 1 MiB | 32 | 6 | disabled | pattern-independent | 979 | 7.20 | 2708 | 2099229 | 0.00% | 0% |
| tcp | 4 KiB | 8 | 8 | zstd | dto | 28993 | 0.42 | 129 | 9238 | 97.18% | 0% |
| tcp | 4 KiB | 32 | 32 | zstd | dto | 30921 | 1.26 | 119 | 9223 | 97.18% | 0% |
| tcp | 4 KiB | 8 | 8 | zstd | mixed | 27072 | 0.54 | 143 | 9281 | 72.18% | 0% |
| tcp | 4 KiB | 32 | 32 | zstd | mixed | 28276 | 2.08 | 139 | 9275 | 72.18% | 0% |
| tcp | 4 KiB | 8 | 8 | zstd | random | 33622 | 0.48 | 110 | 8900 | 0.00% | 100% |
| tcp | 4 KiB | 32 | 32 | zstd | random | 36757 | 1.87 | 108 | 8885 | 0.00% | 100% |
| tcp | 64 KiB | 8 | 8 | zstd | dto | 11106 | 1.15 | 310 | 132422 | 99.82% | 0% |
| tcp | 64 KiB | 32 | 32 | zstd | dto | 11885 | 5.35 | 306 | 132270 | 99.82% | 0% |
| tcp | 64 KiB | 8 | 8 | zstd | mixed | 3775 | 4.28 | 848 | 132796 | 74.23% | 0% |
| tcp | 64 KiB | 32 | 32 | zstd | mixed | 3754 | 12.32 | 943 | 132796 | 74.23% | 0% |
| tcp | 64 KiB | 8 | 8 | zstd | random | 8618 | 1.77 | 419 | 131909 | 0.00% | 100% |
| tcp | 64 KiB | 32 | 32 | zstd | random | 9255 | 4.34 | 357 | 131976 | 0.00% | 100% |
| tcp | 256 KiB | 8 | 8 | zstd | dto | 2906 | 4.66 | 893 | 532028 | 99.95% | 0% |
| tcp | 256 KiB | 32 | 24 | zstd | dto | 3402 | 7.36 | 996 | 527982 | 99.95% | 0% |
| tcp | 256 KiB | 8 | 8 | zstd | mixed | 880 | 10.99 | 2643 | 527968 | 74.31% | 0% |
| tcp | 256 KiB | 32 | 24 | zstd | mixed | 908 | 26.94 | 2562 | 527996 | 74.31% | 0% |
| tcp | 256 KiB | 8 | 8 | zstd | random | 2384 | 4.64 | 1162 | 525540 | 0.00% | 100% |
| tcp | 256 KiB | 32 | 24 | zstd | random | 2318 | 11.40 | 1277 | 525612 | 0.00% | 100% |
| tcp | 1 MiB | 8 | 6 | zstd | dto | 750 | 9.60 | 3076 | 2198531 | 99.98% | 0% |
| tcp | 1 MiB | 32 | 6 | zstd | dto | 949 | 8.83 | 2547 | 2100224 | 99.98% | 0% |
| tcp | 1 MiB | 8 | 6 | zstd | mixed | 220 | 30.89 | 9711 | 2100244 | 74.32% | 0% |
| tcp | 1 MiB | 32 | 6 | zstd | mixed | 220 | 29.32 | 9609 | 2100233 | 74.32% | 0% |
| tcp | 1 MiB | 8 | 6 | zstd | random | 618 | 11.52 | 3988 | 2231134 | 0.00% | 100% |
| tcp | 1 MiB | 32 | 6 | zstd | random | 631 | 11.65 | 4034 | 2100022 | 0.00% | 100% |
| sharedmemory | 4 KiB | 8 | 8 | disabled | pattern-independent | 124023 | 0.12 | 30 | 8725 | 0.00% | 0% |
| sharedmemory | 4 KiB | 32 | 32 | disabled | pattern-independent | 215708 | 0.26 | 14 | 8635 | 0.00% | 0% |
| sharedmemory | 64 KiB | 8 | 8 | disabled | pattern-independent | 47716 | 0.31 | 84 | 131668 | 0.00% | 0% |
| sharedmemory | 64 KiB | 32 | 32 | disabled | pattern-independent | 47872 | 1.53 | 72 | 131636 | 0.00% | 0% |
| sharedmemory | 256 KiB | 8 | 8 | disabled | pattern-independent | 10779 | 1.62 | 306 | 533479 | 0.00% | 0% |
| sharedmemory | 256 KiB | 32 | 24 | disabled | pattern-independent | 8596 | 4.92 | 393 | 623668 | 0.00% | 0% |
| sharedmemory | 1 MiB | 8 | 6 | disabled | pattern-independent | 1964 | 4.84 | 1905 | 2165390 | 0.00% | 0% |
| sharedmemory | 1 MiB | 32 | 6 | disabled | pattern-independent | 1853 | 5.50 | 1743 | 2099722 | 0.00% | 0% |
| sharedmemory | 4 KiB | 8 | 8 | zstd | dto | 45279 | 0.26 | 87 | 9338 | 97.18% | 0% |
| sharedmemory | 4 KiB | 32 | 32 | zstd | dto | 46453 | 0.80 | 85 | 9332 | 97.18% | 0% |
| sharedmemory | 4 KiB | 8 | 8 | zstd | mixed | 42630 | 0.28 | 87 | 9408 | 72.18% | 0% |
| sharedmemory | 4 KiB | 32 | 32 | zstd | mixed | 42239 | 0.87 | 86 | 9417 | 72.18% | 0% |
| sharedmemory | 4 KiB | 8 | 8 | zstd | random | 55753 | 0.20 | 67 | 9056 | 0.00% | 100% |
| sharedmemory | 4 KiB | 32 | 32 | zstd | random | 54591 | 1.49 | 68 | 9044 | 0.00% | 100% |
| sharedmemory | 64 KiB | 8 | 8 | zstd | dto | 13617 | 0.93 | 278 | 132621 | 99.82% | 0% |
| sharedmemory | 64 KiB | 32 | 32 | zstd | dto | 14204 | 2.33 | 282 | 132620 | 99.82% | 0% |
| sharedmemory | 64 KiB | 8 | 8 | zstd | mixed | 4826 | 2.00 | 693 | 132688 | 74.23% | 0% |
| sharedmemory | 64 KiB | 32 | 32 | zstd | mixed | 4888 | 7.42 | 643 | 132652 | 74.23% | 0% |
| sharedmemory | 64 KiB | 8 | 8 | zstd | random | 13431 | 0.71 | 298 | 132315 | 0.00% | 100% |
| sharedmemory | 64 KiB | 32 | 32 | zstd | random | 13568 | 2.65 | 269 | 132347 | 0.00% | 100% |
| sharedmemory | 256 KiB | 8 | 8 | zstd | dto | 3055 | 3.36 | 1036 | 526278 | 99.95% | 0% |
| sharedmemory | 256 KiB | 32 | 24 | zstd | dto | 2981 | 8.95 | 933 | 526299 | 99.95% | 0% |
| sharedmemory | 256 KiB | 8 | 8 | zstd | mixed | 850 | 10.79 | 2735 | 526284 | 74.31% | 0% |
| sharedmemory | 256 KiB | 32 | 24 | zstd | mixed | 871 | 30.31 | 2742 | 526300 | 74.31% | 0% |
| sharedmemory | 256 KiB | 8 | 8 | zstd | random | 3222 | 3.03 | 1022 | 525961 | 0.00% | 100% |
| sharedmemory | 256 KiB | 32 | 24 | zstd | random | 2787 | 10.52 | 1084 | 624312 | 0.00% | 100% |
| sharedmemory | 1 MiB | 8 | 6 | zstd | dto | 875 | 8.43 | 2741 | 2100601 | 99.98% | 0% |
| sharedmemory | 1 MiB | 32 | 6 | zstd | dto | 830 | 9.69 | 2868 | 2100601 | 99.98% | 0% |
| sharedmemory | 1 MiB | 8 | 6 | zstd | mixed | 232 | 28.39 | 9464 | 2102660 | 74.32% | 0% |
| sharedmemory | 1 MiB | 32 | 6 | zstd | mixed | 217 | 34.05 | 9799 | 2100612 | 74.32% | 0% |
| sharedmemory | 1 MiB | 8 | 6 | zstd | random | 835 | 7.74 | 3167 | 2297014 | 0.00% | 100% |
| sharedmemory | 1 MiB | 32 | 6 | zstd | random | 842 | 9.06 | 3343 | 2133050 | 0.00% | 100% |

Localhost is intentionally not used as the sole usefulness decision. Mixed payloads often trade CPU/QPS for substantial wire reduction, while DTO-like payloads are cheap enough to be neutral or favorable in several cases. Random payloads exercise the adaptive raw fallback instead of forcing compression.

## Bandwidth-constrained/WAN-like evidence

The WAN profile uses loopback shaping of 20 ms delay and 50 Mbit/s and records the shaping string in the evidence. It covers 64 KiB, 256 KiB, and 1 MiB at requested concurrency 8 and 32.

| Size | Req c | Eff c | Mode | Pattern | QPS | P50 ms | P99 ms | P99.9 ms | CPU us/op | B/op | Wire savings | Rejection |
|---:|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 64 KiB | 8 | 8 | disabled | pattern-independent | 26.2 | 303.62 | 363.76 | 374.62 | 2044 | 133514 | 0.00% | 0% |
| 64 KiB | 32 | 32 | disabled | pattern-independent | 47.5 | 673.25 | 945.83 | 967.00 | 1076 | 133677 | 0.00% | 0% |
| 256 KiB | 8 | 8 | disabled | pattern-independent | 11.9 | 672.36 | 682.93 | 692.24 | 3746 | 547962 | 0.00% | 0% |
| 256 KiB | 32 | 24 | disabled | pattern-independent | 11.9 | 2017.10 | 2521.90 | 2540.77 | 3414 | 566414 | 0.00% | 0% |
| 1 MiB | 8 | 6 | disabled | pattern-independent | 3.0 | 2016.28 | 2383.40 | 2383.40 | 11746 | 2402836 | 0.00% | 0% |
| 1 MiB | 32 | 6 | disabled | pattern-independent | 3.0 | 2016.46 | 2382.75 | 2382.75 | 10751 | 2173322 | 0.00% | 0% |
| 64 KiB | 8 | 8 | zstd | dto | 195.8 | 40.73 | 42.63 | 43.09 | 1640 | 132673 | 99.82% | 0% |
| 64 KiB | 32 | 32 | zstd | dto | 739.7 | 40.63 | 81.20 | 81.47 | 635 | 132266 | 99.82% | 0% |
| 64 KiB | 8 | 8 | zstd | mixed | 172.0 | 46.14 | 51.85 | 65.51 | 1280 | 132756 | 74.23% | 0% |
| 64 KiB | 32 | 32 | zstd | mixed | 182.6 | 173.86 | 222.71 | 233.61 | 1284 | 132518 | 74.23% | 0% |
| 64 KiB | 8 | 8 | zstd | random | 47.4 | 168.35 | 168.42 | 188.33 | 1239 | 132343 | 0.00% | 100% |
| 64 KiB | 32 | 32 | zstd | random | 47.4 | 673.41 | 856.81 | 967.53 | 1211 | 132304 | 0.00% | 100% |
| 256 KiB | 8 | 8 | zstd | dto | 193.5 | 41.14 | 43.80 | 43.81 | 1545 | 532041 | 99.95% | 0% |
| 256 KiB | 32 | 24 | zstd | dto | 513.8 | 41.24 | 45.42 | 45.56 | 1214 | 525847 | 99.95% | 0% |
| 256 KiB | 8 | 8 | zstd | mixed | 45.9 | 172.94 | 181.96 | 192.90 | 3617 | 526564 | 74.31% | 0% |
| 256 KiB | 32 | 24 | zstd | mixed | 45.7 | 518.84 | 529.45 | 531.02 | 4050 | 531833 | 74.31% | 0% |
| 256 KiB | 8 | 8 | zstd | random | 11.9 | 672.35 | 672.54 | 691.69 | 3784 | 527300 | 0.00% | 100% |
| 256 KiB | 32 | 24 | zstd | random | 11.9 | 2017.19 | 2150.18 | 2192.22 | 3649 | 560170 | 0.00% | 100% |
| 1 MiB | 8 | 6 | zstd | dto | 124.2 | 42.63 | 46.14 | 46.14 | 2946 | 2296517 | 99.98% | 0% |
| 1 MiB | 32 | 6 | zstd | dto | 124.1 | 42.66 | 46.12 | 46.12 | 2932 | 2165685 | 99.98% | 0% |
| 1 MiB | 8 | 6 | zstd | mixed | 11.4 | 517.90 | 536.83 | 536.83 | 11707 | 2102092 | 74.32% | 0% |
| 1 MiB | 32 | 6 | zstd | mixed | 11.4 | 517.93 | 538.37 | 538.37 | 11963 | 2102092 | 74.32% | 0% |
| 1 MiB | 8 | 6 | zstd | random | 3.0 | 2016.61 | 2382.52 | 2382.52 | 13798 | 2437544 | 0.00% | 100% |
| 1 MiB | 32 | 6 | zstd | random | 3.0 | 2016.47 | 2382.55 | 2382.55 | 13626 | 2107880 | 0.00% | 100% |

The value signal is clear under constrained bandwidth. At 1 MiB, raw traffic is about 3 QPS. DTO-like Zstd reaches about 124 QPS with ~99.98% business-byte savings; mixed reaches about 11.4 QPS with ~74.32% savings. Random input is rejected 100% and remains near the raw ~3 QPS path.

## Disabled-compression fast-path A/B

Same-runner A/B run: https://github.com/SunSi12138/SharpLink/actions/runs/34025165413

The baseline is PR Stage 1 head `5a025579`; the candidate is the exact Stage-2 candidate. Both revisions use the same pre-Stage-2 `EchoAsync(string)` RPC, no compression providers, two interleaved rounds per revision, TCP + SharedMemory, all four payload sizes, and requested concurrency `1 / 8 / 32 / 128` with the same queue-stability rule.

Across 32 scenarios, median Stage2/Stage1 ratios were:

| Metric | Median ratio | Interpretation |
|---|---:|---|
| QPS | 1.059 | no systematic throughput regression |
| P99 | 0.975 | lower is better |
| CPU/op | 0.944 | lower is better |
| allocated B/op | 1.00002 | effectively unchanged |

These hosted-runner measurements are evidence against a systematic disabled-path regression, not a performance guarantee for every machine.

## Decision

**Go.**

- Core remains algorithm-free.
- The zero-byte representation contract is now internally consistent and round-trip tested.
- The official .NET 10 Zstd package meets the complete-consumption, malformed/truncated, checksum integrity, bounded decompression, cancellation, thread-safety, wire-identity, package, and NativeAOT requirements.
- Linux, Windows, and macOS deployment gates pass without a separately deployed native zstd library for the .NET 10 provider.
- .NET 11 Preview BCL interoperability is proven in both directions on all three platforms, so the future stable BCL backend does not require a new wire identity based on current evidence.
- Adaptive compression rejects incompressible payloads and preserves raw fallback.
- WAN-like evidence demonstrates material value where bandwidth is constrained.
- Same-runner A/B evidence does not show a systematic regression when compression is disabled.

The remaining .NET 11 work is a release-timing task, not an unresolved wire-contract problem: after .NET 11 GA, add the BCL backend under the existing profile and rerun the same compatibility/platform gates before shipping that target.
