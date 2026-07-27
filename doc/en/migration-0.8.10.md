# SharpLink 0.8.10 Migration Guide

Chinese: [`../migration-0.8.10.md`](../migration-0.8.10.md)

0.8.10 changes no public API, Protocol v2, or generated Manifest. When a custom transport, profile-aware factory, or Codec Adapter fails during both construction and rollback, callers may now receive `AggregateException`; its first cause remains the primary construction failure. No configuration migration is required.
