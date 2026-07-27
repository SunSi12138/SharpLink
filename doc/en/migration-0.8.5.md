# SharpLink 0.8.5 Migration Guide

Chinese: [`../migration-0.8.5.md`](../migration-0.8.5.md)

0.8.5 changes no public API, Protocol v2 wire layout, or generated Manifest version, so Client and Server can be rolled independently.

- `ISharpLinkClientAccessor.GetClientAsync` now consistently fails after host termination, including publication races.
- When a service factory or handler and service/scope cleanup fail together, the Server retains every cause. Custom exception mappers should inspect `AggregateException.InnerExceptions` rather than assume one exception.
- A failed fixed-client initial minimum-pool attempt now releases every established session and settles in `Faulted`; existing retry and Stop behavior remains available.

No configuration migration is required.
