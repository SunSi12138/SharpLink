using TUnit.Core;

// These tests exercise process-wide generated catalogs, pools, and telemetry listeners.
// Keep their mutations deterministic under TUnit's parallel-by-default scheduler.
[assembly: NotInParallel]
