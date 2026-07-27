# SharpLink 0.8.19 Migration Guide

Chinese: [`../migration-0.8.19.md`](../migration-0.8.19.md)

0.8.19 does not change the Protocol v2 wire format or generated Manifests. A custom Server authenticator establishes a connection only when `IsAuthenticated=true` and `ErrorCode=Unknown`. Implementations that call the positional `SharpLinkAuthenticationResult` constructor directly should use `Success` or `Authenticate(context)` for successful results.

Each Client and Server interceptor `next` delegate is now single-use; a second call throws `InvalidOperationException`. An interceptor that needs fan-out should complete its own parallel work before `next` while handing only one logical RPC to the pipeline. SharpLink retry policies continue to own retries.

`SharpLinkAdmissionControlOptions.MaxQueueDelay` is now limited to 2,147,483,647 ms (about 24.8 days), with larger values rejected during configuration. Very long endpoint polling and Client/Server heartbeat intervals remain valid and wait in cancellable slices. Generic Host Server Stop returns `AggregateException` when primary and cleanup failures both occur, and faulted Client background tasks now produce an Error log.
