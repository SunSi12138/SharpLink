# SharpLink 0.8.13 Migration Guide

Chinese: [`../migration-0.8.13.md`](../migration-0.8.13.md)

0.8.13 changes no public API, Protocol v2, or generated Manifest and requires no configuration migration. PipeReader still permits only one pending read: a concurrent second call continues to receive `InvalidOperationException`, but it no longer changes the accepted read's cancellation or notification state. Cancellable shared-memory read/write waits are now woken promptly by their own token. Closing a control channel or a PipeWriter with spill now waits for the related background writer/flush operation to converge instead of leaving resource work running after completion returns.
