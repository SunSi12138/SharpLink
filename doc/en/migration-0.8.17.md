# SharpLink 0.8.17 Migration Guide

Chinese: [`../migration-0.8.17.md`](../migration-0.8.17.md)

0.8.17 does not change the Protocol v2 wire format or generated Manifests. A handshake request's `RequiredCapabilities` must now be a subset of `SupportedCapabilities`. Unknown request capabilities remain subject to negotiation, while unknown negotiated response capabilities are rejected as a protocol violation.

Runtime configuration now has aggregate safety bounds: `RuntimeConcurrencyOptions.StripeCount` is limited to 1,024; `StripeCount × InitialMapCapacityPerStripe` is limited to 1,048,576 entries; and `BufferWriterPoolOptions.MaxPooledWriters × MaxRetainedCapacityBytes` is limited to 64 MiB. Deployments above these bounds should reduce preallocation/retention or distribute load across more Runtime Contexts. Defaults are unaffected.

TLS and partition admission configuration is now deep-cloned at the build boundary. Mutating the original `X509ChainPolicy` or partition concurrency/rate-limit options after Build no longer changes a live Client or Server; publish a newly built instance to change them. Concurrent unregister calls for the same multi-cluster dynamic assembly now share one operation and result, while each caller may still cancel its own wait independently.
