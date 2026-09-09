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


## Follow-up: protect the no-metadata path (2026-09-09)

The historical +5.12% was a two-parse component control, not a measured default
RPC QPS regression. Both control variants called the unchanged Read parser.
Repeated measurements, including A/A and thread-CPU-clock diagnostics, were
non-stationary; they do not establish a fixed 5.12% production penalty.

The conservative revision makes the inline no-metadata branch explicit:
DecodeInboundPayload -> retained input Dispose -> CompleteDecode -> original
ReadRequestEnvelope. It does not snapshot encodedPayload, compare prefix bytes,
or invoke ReadDecoded. Non-compressed requests still bypass this entire block.
One HasMetadata flag test remains inside the compressed branch; this is not a
claim that generated assembly or all end-to-end costs are identical to the parent.
The metadata branch retains its original reuse and live-owner comparison order.
ServerRequestEnvelopeReader.cs itself is unchanged from the original PR.

A broader no-metadata prefix-rebind experiment was rejected: favorable initial
samples did not survive all repeated controls, including CPU-clock measurements.
It is NOT included in this commit, and its best-case figures must not be used as
performance evidence for this conservative revision.

The final product revision built in Release with zero warnings and errors. A
focused host compiling repository test sources passed 28/28 tests normally and
28/28 with hardware intrinsics disabled: 13 existing envelope cases, 9 existing
compression cases, and 6 new no-metadata cases. These cover timed/untimed inputs,
mutations, truncation, segmentation, exact deadline retention, and actual session
decompression with the existing RLE test provider. They are not a complete
Unary/Oneway admission/lease-release race integration suite.

The local production sources match this commit. Upstream stack changes observed
before publication affected documentation and unrelated tests, not product code.
Publication refuses a moved head and uses a normal fast-forward push.
No final TCP QPS/p99 run or quantified elimination of the historical 5.12% is
claimed. Default-path end-to-end non-inferiority remains an acceptance condition.
