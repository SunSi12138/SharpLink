# Generated stream snapshot ownership regression

This isolated correctness change is based on dev `f8ba89121c87a6410a0139c44d7db30eab877f41`. It contains neither the B2 pilot nor directional-lock changes.

`run.py` builds the fixed old dev and the actual checked-out commit with the identical two-test fixture. It does not transform production code. The old arm must reproduce both exact failure observations: a release callback invoked twice with an unpublished writer left active, and an original serialization exception replaced by a cleanup exception with its writer left active. An arbitrary failure or compilation failure is not accepted as red.

The current arm must pass the two focused tests four times, then the full 2,439-case regular unit suite four times, with no skips. All process exits, original logs, fresh TUnit reports, source trees, product differences, and binary hashes are retained. An unexpected result fails the workflow and is never retried by the collector.

The fix transfers snapshot ownership before invoking user cleanup. The exception path preserves an existing operation failure if snapshot release also fails, and a nested finally always attempts to return an unpublished writer. The tests do not assert that an arbitrary custom buffer pool whose Return method itself throws will preserve the earlier exception.

The runner is a correctness regression, not a performance or NativeAOT result. Original CI workflows and thresholds are unchanged.

The regular expectation is 2,444 discovered entries minus five `[Explicit]` workers,
not a full-run result. The first full execution must confirm it; mismatches are
investigated without weakening equality. See the [inventory reconciliation](../../doc/cooperative-maintenance-dev-integration.md#discovery-and-regular-execution-inventory).

The #933 repair adds four named Codec-publication cases: unrelated retirement,
removed referenced provider rejection, same-assembly surviving provider acceptance,
and competing publication rejection. The 16ef hosted run executed 2,435 regular
cases; adding exactly these four predeclares 2,439 (2,447 with the eight lease
controls). Historical negative-control counts are unchanged.
