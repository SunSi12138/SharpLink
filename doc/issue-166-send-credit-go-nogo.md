# Issue #166 send-credit ordering: completion and Go/No-Go

This document closes the remaining Definition-of-Done gaps for
[#166](https://github.com/SunSi12138/SharpLink/issues/166). PR
[#188](https://github.com/SunSi12138/SharpLink/pull/188) merged the core
implementation; this follow-up adds the correctness coverage and records the
evidence needed to justify the final decision.

## Implementation already on `dev`

The generated stream path now computes an exact encoded size first, awaits flow
control credit, and only then rents the frame writer and serializes:

```text
TryGetEncodedSize(item)
    -> await stream send credit
    -> rent writer
    -> SerializeSized once
    -> validate actual == predicted
    -> SendPacket
```

Codecs that do not expose `IRpcSizedCodec<T>` keep the original serialize-first
path unchanged. This preserves a single-serialization rule and does not require
every serializer to support exact sizing.

The implementation lives in:

- `src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs`
- `src/SharpLink.Runtime/RpcSessionExtensions.cs`

## Correctness coverage added

The new tests in
`test/SharpLink.UnitTests/Runtime/SendStreamChunkKnownSizeTests.cs` cover the
correctness properties required by #166:

- sized path emits a byte-for-byte identical `StreamData` frame as the fallback;
- predicted/actual size mismatch fails safely and returns unsent credit exactly once;
- `SerializeSized` failure after credit acquisition returns credit exactly once;
- cancellation before credit neither serializes nor debits credit;
- cancellation during sized serialization returns credit exactly once;
- zero-sized payloads debit exactly one flow-control byte;
- maximum-frame-payload boundary items round-trip unchanged;
- repeated sized sends leave no credit leak.

The existing
`GeneratedServerBridgeTests.SizedOutboundPumpShouldNotSerializeBeforeCredit`
covers the slow/credit-exhausted path: no serialization occurs until credit
arrives, then exactly one data frame and one terminal frame are published.

## Evidence

### Bytes held while waiting for credit

`SendCreditBufferHoldEvidenceRunner` measures the old serialize-first path
against the new exact-size path for a generated string DTO. On macOS arm64,
Release, .NET 10, the old path holds the full encoded item (with backing
capacity roughly twice the payload) before credit, while the new path holds
zero bytes before credit:

| case | old path held bytes | old path capacity | exact size supported | new path held bytes |
| --- | ---: | ---: | ---: | ---: |
| 1 KiB | 1024 | 2048 | yes | 0 |
| 16 KiB | 16384 | 32768 | yes | 0 |
| 64 KiB | 65536 | 131072 | yes | 0 |
| 256 KiB | 262144 | 524288 | yes | 0 |
| 1 MiB | 1048576 | 2097152 | yes | 0 |

Exact sizing was supported for every sampled size from 1 KiB through 1 MiB.

### Fast-consumer cost

`SendCreditFastPathEvidenceRunner` compares the two serialization paths on a
generated nested DTO (200,000 iterations, warmed, Release, .NET 10, arm64):

| path | ns/op | B/op |
| --- | ---: | ---: |
| serialize-first | 557.95 | 0.000 |
| sized path | 531.75 | 0.000 |

The sized path is allocation-free and does not regress the fast consumer;
on this machine it was slightly faster.

## Final decision

**Go** for the generated exact-size path, with the serialize-first path retained
as the fallback for codecs that cannot provide an exact size.

This satisfies:

- exact size is obtained without double serialization;
- credit acquisition happens before renting or serializing the item;
- serialize/send/mismatch failures refund unsent credit exactly once;
- fallback codecs keep the original behavior;
- the slow/credit-starved path no longer holds encoded item buffers;
- fast-consumer throughput/allocations are not regressed.

The issue's stated non-goals remain unchanged: no double serialization, no
upper-bound reservation/refund scheme, no `StreamFlowController` algorithm
rewrite, no Protocol v2 wire change, and no change to default receive/send
window sizes. SendPump byte-capacity backpressure remains the separate concern
tracked in #163.
