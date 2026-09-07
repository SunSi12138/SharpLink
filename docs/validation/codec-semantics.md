# Codec validation — #558 raw DateTime contract + #559 measurements

Refs #558 and #559. Original characterization baseline: `dev@acb160faa72a07835b01d049a2fbcf9070b061df`.

```sh
dotnet build test/SharpLink.UnitTests -c Release
python3 eng/validate-codec-semantics.py --mode regression
```

## DateTime contract (#558)

SharpLink 2.0 treats `DateTime` as a fixed raw value representation. The observable contract is to preserve `Ticks` and `Kind`; SharpLink does **not** reinterpret `DateTimeKind.Local` to preserve the same UTC instant when producer and consumer use different local time zones.

Consequences:

- `Utc`: ticks and `Utc` kind are preserved, so the instant is stable.
- `Local`: wall-clock ticks and `Local` kind are preserved. A consumer in another time zone can therefore obtain a different `ToUniversalTime()` instant from the same transmitted value.
- `Unspecified`: ticks and `Unspecified` kind are preserved. No cross-zone instant meaning is implied.
- Values that represent a system boundary instant should be normalized to UTC before transmission, or represented as `DateTimeOffset` when an offset is part of the domain value.

This intentionally keeps the existing raw semantics used by built-in DateTime collections and generated DTO fixed DateTime fields. The production fix changes only the top-level scalar and nullable DateTime codecs from `ToBinary`/`FromBinary` behavior to that same raw representation; it does not replace collection codecs or add a compatibility path.

SharpLink 2.0 is still the development line and already does not promise wire compatibility with 1.1.x or intermediate 2.0 development artifacts, so this fix does not add a legacy decoder, protocol-version branch, or separate CodecHash compatibility generation solely for the old scalar behavior.

### Regression matrix

The real `RpcCodecProvider` resolves scalar, nullable, array, List, Memory, ReadOnlyMemory and ImmutableArray codecs. Six producer processes cover UTC/Tokyo and Utc/Local/Unspecified. Every payload is decoded by independent consumers in both zones: 12 cross-process comparisons, plus six producer roundtrip controls. The process verifies its actual timezone offset before results are accepted.

Regression mode requires every DateTime route to preserve the producer's `ticks + Kind`. It records UTC ticks as evidence but deliberately does not require Local/Unspecified UTC ticks to remain equal across zones. This catches any future reintroduction of instant-preserving scalar semantics while collections remain raw.

The test input is January 15, 2026, away from DST transitions. DST ambiguity/invalid local times, cross-runtime layout compatibility and big-endian compatibility are outside this #558 regression. Generated DTO DateTime fields already use `RpcGeneratedCodecWire.WriteDateTime` / `ReadDateTime` raw fixed-value semantics and are source-audited here rather than being routed through the runtime scalar codec.

### Original evidence

[Run 34047978157](https://github.com/SunSi12138/SharpLink/actions/runs/34047978157), head `a3e8758d4bbdf2bdf0c8c5e1c9542f87e53819fe`, .NET 10.0.11 / SDK 10.0.400, Ubuntu 24.04.4, Release. The original codec step succeeded.

[Raw JSON, payloads and worker logs](https://github.com/SunSi12138/SharpLink/actions/runs/34047978157/artifacts/9993699824).

Before the #558 fix, both Local cross-zone directions disagreed by exactly 9 hours between scalar and array/List. Scalar preserved the source UTC instant through `ToBinary`/`FromBinary`; collections preserved source wall-clock ticks through raw blit. The other 10 consumer comparisons agreed and all six producer roundtrip controls passed. This discrepancy is the regression being removed.

## DateTimeOffset (#559)

24 Release measurement cells: array/List, 64/256/1024 values, contiguous/64-byte/7-byte/1-byte fragments. Each pair uses identical valid bytes. Segment construction, array creation and exact roundtrip checking (including offset, not only instant) occur outside timed loops. Each cell has three warmups and seven reported samples, and includes per-operation allocations. The full sequence is passed by `in`; the codec enforces exact size and does not expose a consumed-position cursor. We verify unchanged input length rather than inventing a consumption measurement.

Current source evidence is in `src/SharpLink.Runtime/Codec/CodecHelpers.cs`, `ReadDateTimeOffsetCollection`: each iteration calls `payload.Slice(index * 16, 16)` from the original sequence start. Fragmented sequences therefore repeatedly traverse earlier segments. Spanning elements copy into a 16-byte stack buffer. `DateTimeOffsetListCodec.Deserialize` in `StructCodec.cs` first obtains the intermediate array, then returns `[.. array]`, adding the list backing storage and a second element copy. These are separate costs: stack copies do not imply additional per-fragment managed allocation. The measurements do not instrument exact segment traversal counts and do not turn noisy timings into asymptotic proofs.

Inspect raw medians, ranges and allocation data across sizes/fragments; do not report an unimplemented candidate's improvement percentage. This is not BenchmarkDotNet, a statistically isolated hardware comparison, or an end-to-end RPC throughput experiment. No timing threshold makes CI flaky. All elapsed-time values are evidence attached to the runtime/OS/architecture reported by the worker. Whether an optimization is worthwhile is the maintainer's subsequent decision.

Original DateTimeOffset array medians from run 34047978157, microseconds per decode (all exact roundtrips passed):

| Elements | Contiguous | 64-byte fragments | 7-byte fragments | 1-byte fragments |
|---:|---:|---:|---:|---:|
| 64 | 3.60 | 6.18 | 24.86 | 113.94 |
| 256 | 13.72 | 41.76 | 220.78 | 803.86 |
| 1024 | 44.08 | 236.36 | 1815.51 | 12489.33 |

At 1024 elements, the array allocated 16,408 B/decode and List allocated 32,848 B/decode in every fragmentation configuration. These numbers remain #559 evidence only; #558 does not optimize DateTimeOffset.
