# OneWay internal shape split (#729)

The client now dispatches once by `HasClientStreams` into plain and streaming async cores.
This keeps the public/generated channel ABI, metadata/module wrappers and lifecycle rules unchanged.
The runtime change is +29 lines and remains below the existing 1,100-line allowance.
The permanent regression test checks descriptor-based producer ownership, wire flags, owned cancellation, and lease/admission cleanup.

## Measured benefit and cost

[Research source](https://github.com/SunSi12138/SharpLink/tree/6ece2f7a7aaa8b3650885124e26608653cb77684)
compares frozen baseline A (`553854f56c33a6e02ef225c24f558e5e92037aa1`), internal split C, and experimental static entries B.
[Hosted run 37945758812](https://github.com/SunSi12138/SharpLink/actions/runs/37945758812)
uses .NET SDK 10.0.112/runtime 10.0.12 on Ubuntu x64.

The actual generated invocation fixture passed all 216 controlled and 108 tiered-PGO samples.
Paired process-allocation C-minus-A medians for suspended 0/1/2-stream calls were
-48.024/-7.986/-8.165 B/op (12 pairs) and -47.957/-8.036/-8.276 B/op (six tiered pairs).
Every suspended pair improved. Synchronous caller allocation was essentially unchanged.
These are measured allocations, not conversions from state-struct size.
All C/A timing confidence intervals include 1; no latency improvement is claimed.

The full-RPC fixture's 64-bit scalar/no-stream state shrinks 248 to 192 inline bytes;
one/two-stream states shrink 256/272 to 248/264 bytes.
The local allocation fixture uses a different, 32-bit request.
MoveNext IL grows in aggregate from 1,781 to 986 + 1,092 bytes (+16.7%).
In this six-shape fixture, NativeAOT executable size grows 25,072 bytes (+0.243%),
and strict core native code grows 21,551 to 27,688 bytes (+28.5%).
Individual executed cores shrink, but generic instantiations emit both branches.
Different contract catalogs can therefore have different image-size tradeoffs.

All 324 full-RPC cases passed service-completed call/item counts and feature checks:
TCP/SharedMemory, JIT/NativeAOT, 0/1/2 streams, batch 1/8, deadlines, cancellation,
interceptors, telemetry, endpoints and admission. This is a one-round regression sanity check,
not statistical proof of zero regression or end-to-end acceleration.
Noisy individual timing outliers remain; median C/A wall ratios are 1.0013 JIT and 1.0010 AOT.

The allocation benefit and bounded runtime change justify C under the updated local-benefit criterion.
B's additional generated/runtime compatibility burden has no demonstrated incremental benefit.
Research projects, experimental patches and expensive workflows stay in
[Draft research PR #795](https://github.com/SunSi12138/SharpLink/pull/795), outside this production change.
The original archive is SHA-256 `48c10e5d5244a715251be0bba1ae23ee7de435be96753e191f99adc0e28ecc89`;
artifact-only [recovery run 37951510883](https://github.com/SunSi12138/SharpLink/actions/runs/37951510883)
verified that digest and retained original paired rows/disassembly without rerunning measurements.
Large Actions artifacts have seven-day retention; the research PR links the durable selected-evidence snapshot.
