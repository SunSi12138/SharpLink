# Codec evidence — semantics and measurements, not a wire change

Refs #558 and #559. Baseline `dev@acb160faa72a07835b01d049a2fbcf9070b061df`.

```sh
dotnet build test/SharpLink.UnitTests -c Release
python3 eng/validate-codec-semantics.py --mode characterize
python3 eng/validate-codec-semantics.py --mode regression
```

## DateTime

The real `RpcCodecProvider` resolves scalar, array and List codecs; their concrete types and base64 payloads are persisted. Six producer processes cover UTC/Tokyo and Utc/Local/Unspecified. Every payload is decoded by independent consumers in BOTH zones: 12 cross-process comparisons, plus six producer roundtrip controls. The process verifies its actual timezone offset before results are accepted. Tests compare ticks, Kind AND UTC ticks, not DateTime.Equals alone.

Characterization expects Local scalar to preserve the instant while collections preserve local wall-clock ticks across zones. Same-zone, Utc and Unspecified values are controls. This is a description to be verified, not a new contract. Regression mode asks whether scalar and collections agree; it intentionally does not choose the eventual correct wire representation. The input is January 15, 2026, away from DST transitions. DST ambiguity/invalid local times, generated DTO routes, Memory/ImmutableArray routes, cross-runtime and big-endian compatibility are NOT covered by these experiments.

## DateTimeOffset

24 Release measurement cells: array/List, 64/256/1024 values, contiguous/64-byte/7-byte/1-byte fragments. Each pair uses identical valid bytes. Segment construction, array creation and exact roundtrip checking (including offset, not only instant) occur outside timed loops. Each cell has three warmups and seven reported samples, and includes per-operation allocations. The full sequence is passed by `in`; the codec enforces exact size and does not expose a consumed-position cursor. We verify unchanged input length rather than inventing a consumption measurement.

Current source evidence is in `src/SharpLink.Runtime/Codec/CodecHelpers.cs`, `ReadDateTimeOffsetCollection`: each iteration calls `payload.Slice(index * 16, 16)` from the original sequence start. Fragmented sequences therefore repeatedly traverse earlier segments. Spanning elements copy into a 16-byte stack buffer. `DateTimeOffsetListCodec.Deserialize` in `StructCodec.cs` first obtains the intermediate array, then returns `[.. array]`, adding the list backing storage and a second element copy. These are separate costs: stack copies do not imply additional per-fragment managed allocation. The measurements do not instrument exact segment traversal counts and do not turn noisy timings into asymptotic proofs.

Inspect raw medians, ranges and allocation data across sizes/fragments; do not report an unimplemented candidate's improvement percentage. This is not BenchmarkDotNet, a statistically isolated hardware comparison, or an end-to-end RPC throughput experiment. No timing threshold makes CI flaky. All elapsed-time values are evidence attached to the runtime/OS/architecture reported by the worker. Whether an optimization is worthwhile is the maintainer's subsequent decision.

## Observed codec evidence

[Run 34047978157](https://github.com/SunSi12138/SharpLink/actions/runs/34047978157), head `a3e8758d4bbdf2bdf0c8c5e1c9542f87e53819fe`, .NET 10.0.11 / SDK 10.0.400, Ubuntu 24.04.4, Release. The codec step succeeded. The overall run failed because the new logger control inspected an asynchronously scheduled Task wrapper too early; this is a harness error, not a codec failure. The follow-up test correction inspects the original ValueTask's status instead.

[Raw JSON, payloads and worker logs](https://github.com/SunSi12138/SharpLink/actions/runs/34047978157/artifacts/9993699824).

DateTime: both Local cross-zone directions disagree by exactly 9 hours between scalar and array/List. Scalar preserves source UTC ticks; collections preserve source wall-clock ticks. The other 10 consumer comparisons agree; all six producer roundtrip controls passed. Unspecified agrees across codec paths but does not imply a cross-zone instant-preservation guarantee.

DateTimeOffset array medians, microseconds per decode (all exact roundtrips passed):

| Elements | Contiguous | 64-byte fragments | 7-byte fragments | 1-byte fragments |
|---:|---:|---:|---:|---:|
| 64 | 3.60 | 6.18 | 24.86 | 113.94 |
| 256 | 13.72 | 41.76 | 220.78 | 803.86 |
| 1024 | 44.08 | 236.36 | 1815.51 | 12489.33 |

At 1024 elements, the array allocated 16,408 B/decode and List allocated 32,848 B/decode in every fragmentation configuration. The measurements support substantial fragmentation cost and additional List allocation, but do not quantify a not-yet-implemented optimization's benefit. These recorded numbers belong to this run, not to a universal hardware-independent performance guarantee.
