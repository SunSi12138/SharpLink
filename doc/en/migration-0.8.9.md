# SharpLink 0.8.9 Migration Guide

Chinese: [`../migration-0.8.9.md`](../migration-0.8.9.md)

0.8.9 changes no public API, Protocol v2, or generated Manifest. Repeated Stop/Dispose calls on Hosted Clients and asynchronous server listeners now await the same terminal result; multiple listener-owner failures may surface as an aggregate exception. No configuration migration is required.
