# SharpLink 0.8.40 deep audit

Using exact 0.8.39 commit `8fffab7` as the baseline, this batch confirmed five P2 improvements: empty generated invocation categories leaked legacy `RpcException`/`Internal`; `SharpLinkException` accepted codes that Protocol v2 cannot serialize; Client and Server interceptors could orphan an invoked incomplete continuation; and generated response nullability was lost in C# signatures and unenforced at service and Client short-circuit boundaries.

The fixes use structured `Unimplemented` and remove `RpcException`, validate concrete wire error codes at construction, join discarded incomplete continuations on both sides while preserving direct-forward fast paths, and separate nullable C# display types from stable protocol type identity. Required unary and stream results reject null while nullable controls remain valid.

Pre-fix, all 118 established Generator tests passed while only the new empty-category witness failed; targeted Abstractions preserved 21 established passes and failed exactly the two new public-surface/code witnesses; and the Interceptor Integration class preserved 14 established passes and failed exactly four new join/nullability/mapper witnesses while generated code produced CS8613/CS8604. Final non-incremental Release has zero warnings/errors, Generator is 119/119, Unit 486/486, and Integration 250/250.

Packed descriptor flags reduce `RpcMethodDescriptor` from 48 to 40 bytes while preserving old and new deconstruction shapes. A real TCP interceptor harness changed its median-of-process medians from 39.845 to 40.234 microseconds (+0.98%, overlapping ranges), and allocation fell from approximately 1,584 to 1,560 B/op. The no-interceptor path remained approximately 320 B/op.

A 120-second shared-memory Chaos run completed 817,230 successes, 318,950 expected failures, zero unexpected failures, and 23 restarts with zero Client/Server Errors, 222 ms maximum recovery, and all five final metrics at zero. NativeAOT TCP printed `AOT_SMOKE_PASS transport=tcp`; seven pre-commit packages and fresh-cache TCP/shared-memory functional smoke passed. This round found improvements, so the clean-round counter remains 0/3.
