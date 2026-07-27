# SharpLink 0.8.8 Migration Guide

Chinese: [`../migration-0.8.8.md`](../migration-0.8.8.md)

0.8.8 changes no public API, Protocol v2, or generated Manifest. Anonymous-pipe and shared-memory connections now release every owned resource after an earlier cleanup failure; dynamic-module and server-wide cleanup may surface `AggregateException` when multiple owners fail. No configuration migration is required. Diagnostic code that assumed one cleanup exception should inspect all inner causes.
