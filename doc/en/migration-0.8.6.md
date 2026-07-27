# SharpLink 0.8.6 Migration Guide

Chinese: [`../migration-0.8.6.md`](../migration-0.8.6.md)

0.8.6 changes no public API, Protocol v2, or generated Manifest. Multi-resource cleanup may now throw `AggregateException` containing every cause. A SharpLink Server background run-loop failure now requests Generic Host shutdown; use existing logging and process restart policy. No configuration migration is required.
