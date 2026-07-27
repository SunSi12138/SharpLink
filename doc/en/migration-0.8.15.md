# SharpLink 0.8.15 Migration Guide

Chinese: [`../migration-0.8.15.md`](../migration-0.8.15.md)

0.8.15 changes no public API, Protocol v2, or generated Manifest. Objects passed to Client `UseTransport`, `UseEndpointResolver`, and Server `UseTransport` are single-owner resources: one successful Build (or a failed Build after Runtime Context creation) removes the resource from the builder. Supply a new transport/resolver before building again. Static `UseEndpoint(s)` builders remain reusable because every Build asks the endpoint delegate for fresh factories. Server `Transport` returns null after ownership transfer.

A Unix-domain listener no longer removes an existing path automatically. Normal disposal still removes the path it successfully bound; a stale socket or another existing entry must be explicitly removed by the deployment/process owner after verification. This prevents configuration mistakes from replacing ordinary files or stealing another process's socket name.
