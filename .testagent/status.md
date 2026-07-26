# 0.8.1 test status

## Pre-fix evidence

- Unit: 339 total, 3 failed for mutable authorization scopes, mutable endpoint snapshots, and undisposed resolver cancellation sources.
- Generator: 83 total, 2 failed for exposed manifest arrays and semantic request values bypassing built-in Codecs.
- `Rpc_SumList` baseline allocation: 560 B/op at 16 items and 2480 B/op at 256 items.

## Assertion and pseudo-mutation review

- Authorization tests mutate then restore the old backing sets, proving both direct privilege injection and shared-empty-set contamination without leaking state to other tests.
- Topology tests distinguish a writable array from a wrapper whose mutation throws `NotSupportedException`.
- Generated-source checks cover top-level contracts/services/Codecs/dependencies, nested methods/service dependencies, and cluster routes.
- Boolean checks cover canonical serialization plus both request decode implementations; ordinary raw numeric types remain inline.
- Resolver tests verify the owned CTS is disposed rather than only cancelled. Operation admission and disposal now share one gate, and every concurrent dispose observes the same Task.
- Existing List Codec tests retain null/empty/positive, contiguous/segmented, trailing, invalid-length, and overflow coverage.

## Performance

- Three alternating A/B rounds: 16 items retained 99.56% throughput and reduced 560 → 472 B/op; 256 items reached 102.53% throughput and reduced 2480 → 1432 B/op.
- Invalid no-measurement runs (compiler code 139 and duplicate benchmark project discovery) were excluded and documented.

## Final gate

- Versioned Release build passed with 0 warnings and 0 errors.
- Generator 83/83, Unit 339/339, and Integration 227/227 passed.
- The seven runtime hot-path allocation counts were unchanged. Their absolute latency run was excluded from the gate because every unrelated benchmark shifted by roughly the same host-wide factor; the affected List path instead passed the stricter three-round alternating A/B gate above.
- `git diff --check` passed.
