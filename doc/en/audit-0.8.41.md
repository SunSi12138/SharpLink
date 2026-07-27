# SharpLink 0.8.41 deep audit

Using exact 0.8.40 commit `dd431f5` as the baseline, this batch confirmed five P2 improvements.

| Severity | Proven issue | Fix |
|---|---|---|
| P2 | Non-interceptor unary/client-streaming response paths accepted null decoded by a Codec even when the generated contract declared a required response. | `ResponseNullable` now flows through every request-operation rent, retry, and completion path; required null fails as structured `DataLoss`, while the nullable control still returns null. |
| P2 | The Client ServerStreaming/DuplexStreaming receive dispatcher enqueued required null items. | Client response streams pass generated `ResponseNullable` into the shared dispatcher, which rejects required null at the decode boundary. |
| P2 | The Server ClientStreaming/DuplexStreaming receive dispatcher did not know the parameter's `PayloadNullable` and likewise accepted required null items. | Generated Stubs pass nullability per stream parameter; explicit `IAsyncEnumerable<T?>` remains valid. |
| P2 | Runtime method fingerprints omitted response nullability, so separately compiled required and nullable response contracts appeared to have the same identity. | Nullable response schema now contributes to method/service/contract fingerprints; method IDs, wire types, payload layouts, and established required fingerprints are unchanged. |
| P2 | `Unknown` is reserved for unset local state and 0.8.40 rejected it for service errors, but Protocol v2 could still write and accept the wire code. | Error writers, validators, and readers uniformly accept only concrete defined codes; a rejected write leaves the destination untouched. |

Before the fixes, all 119 established Generator tests passed and only the new cross-compilation response-fingerprint witness failed. All 486 established Unit tests passed and exactly four new witnesses failed: required scalar null, Client response-stream null, Server request-stream null, and the bidirectional reserved-`Unknown` protocol boundary. Required/nullable, writer/raw-reader, and concrete-code round-trip controls cover partial fixes and accidental rejection of valid values.

After the fixes, non-incremental Release produced zero warnings/errors; Generator passed 120/120, Unit 490/490, and Integration 250/250. Five-process real TCP medians were 38.694 -> 38.832 microseconds (+0.36%) without interceptors and 39.911 -> 40.302 microseconds (+0.98%) with one Client and Server interceptor; ranges overlapped and allocation remained approximately 320 B/op and 1,560 B/op respectively. The required-reference stream-dispatcher three-process median was exactly 13.860 -> 13.860 ns/op with 1.333 B/op on both sides.

The final 120-second shared-memory Chaos run completed 815,964 successes, 316,929 expected failures, zero unexpected failures, and 23 restarts; Client/Server Error logs were empty, maximum recovery was 221 ms, and drain plus all five active gauges reached zero. NativeAOT TCP printed `AOT_SMOKE_PASS transport=tcp`; all seven 0.8.41 packages packed successfully, and fresh-cache TCP/shared-memory functional smoke passed. This round found new improvements, so the consecutive clean-audit counter remains 0/3.
