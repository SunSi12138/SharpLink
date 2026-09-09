# Stack continuation 7: reuse metadata after inline request decoding

## Scope and ownership

Base: PR #639, `71004591d9b7764abba2dde67bdbf66d0682d713`, tree
`a103ff08fac41e97f089bd5423fb2dfdc3aae90e`.
Stack: #633 -> #634 -> #635 -> #636 -> #638 -> #639 -> this change.

The inline compressed Unary and Oneway dispatch paths previously parsed the
request envelope before decoding and again after decoding. With metadata this
constructed the same immutable metadata twice. `ReadDecoded` reuses the first
metadata and exact resolved deadline, rebinding only Arguments to decoded storage.

Reuse requires the Metadata capability, HasMetadata, a complete contiguous prefix
in both sequences, byte-for-byte prefix equality, and a metadata length satisfying
the current limit. Fragmented, changed, or truncated prefixes fall back to the
original parser. The first strict parse, decoding, payload limits, and admission
checks remain. No cache field or retained array is added.

Comparison and rebinding happen before disposing the retained encoded payload or
completing its decode permit. Both owners must still be alive at that point. The
persistent asynchronous decoder is deliberately unchanged: its input ownership
lifetime cannot be extended by retaining a sequence view across await.

## Prior incremental component evidence

This table is the previous study's evidence, not a new benchmark run. SDK
10.0.400, runtime 10.0.11, Release, default tiered JIT, one ABBA. Same probe,
Client/Runtime/Abstractions assemblies; only Server implementation differs.
Measurement includes the first parse and second parse/rebind, but excludes real
compression/decompression, service invocation, output and network.

| Two-stage envelope | Original ns | Candidate ns | Change | Allocation B |
| --- | ---: | ---: | ---: | ---: |
| No metadata control | 99.05 | 104.13 | +5.12% | 0 -> 0 |
| One metadata entry | 252.89 | 180.18 | -28.75% | 304 -> 152 |
| Eight metadata entries | 745.55 | 454.26 | -39.07% | 1760 -> 880 |

The no-metadata control called the original parser on both sides, without entering
ReadDecoded, but the measured regression remains a negative result. JIT/layout or
noise is only a possible explanation. The candidate is not proven non-inferior
for every configuration. Saving a second 880-byte metadata snapshot is not a
halving of whole-RPC allocation and does not apply to plain successful Add calls.
Major end-to-end hot-path acceptance remains against v1.1.1; this table compares
the existing strict implementation with the incremental candidate.

## Submission verification

The three production files match the previous measured candidate's SHA256 values.
This submission adds `DecodedRequestMetadataReuseTests`, moving the retained
independent 2,924-case validation corpus into a repository TUnit test and adding
a negotiated-capability fallback test. Cases include optional fields, current
limit tightening, truncation, split prefixes, byte-by-byte prefix mutations,
metadata reference reuse, exact deadline retention, and decoded argument aliasing.

A small local TUnit host compiles the same new repository test source and the
13 existing ServerRequestEnvelopeReader test instances: 15/15 passed. Its Release
build finished with zero warnings and zero errors under SDK 10.0.400/runtime
10.0.11. The corpus count is not the number of TUnit tests. The first test-host
compile missed a namespace import; that import was fixed without changing product
code, disabling assertions, or increasing timeouts. No full solution, long CI,
real compressed-dispatch ownership integration, or new TCP QPS/p99 run is claimed.

This remains a review candidate. Real inline dispatch exception, backpressure,
lease-release and combined no-metadata performance checks remain outstanding.
The publication workflow checks the complete source tree and only creates the
new stack branch; it does not modify dev/main, #605, or earlier stack branches.
