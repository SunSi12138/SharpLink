# SharpLink 0.8.12 Migration Guide

Chinese: [`../migration-0.8.12.md`](../migration-0.8.12.md)

0.8.12 changes no public API, Protocol v2, or generated Manifest. Resources handed to Client through `UseTransport` and `UseEndpointResolver` are now released after construction failure; do not reuse the same transport or resolver instance after a failed build. When a custom logger, Codec Adapter, or another construction extension and rollback both fail, callers may receive `AggregateException`, ordered with the original build failure first. No configuration migration is required.
