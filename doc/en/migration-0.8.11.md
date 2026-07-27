# SharpLink 0.8.11 Migration Guide

Chinese: [`../migration-0.8.11.md`](../migration-0.8.11.md)

0.8.11 changes no public API, Protocol v2, or generated Manifest. Ordinary dynamic assembly registration and replacement rejections still return structured errors. Only when a custom Codec Adapter or candidate service also fails during transaction rollback can callers now receive `AggregateException`, with the transaction rejection first. A profile-aware Server transport binding failure now also releases the new Runtime Context. No configuration migration is required.
