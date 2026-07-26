# 0.8.12 regression-test research

## Target inventory and verified candidates

- `src/SharpLink.Client/SharpClientBuilder.cs`: direct `UseTransport` profile binding and constructor failure do not release the Client-owned transport; dynamic resolver validation failure does not release the Client-owned resolver.
- `src/SharpLink.Server/SharpLinkServerBuilder.cs`: service validation lets Runtime Context cleanup replace the primary failure; logger construction occurs after the old rollback boundary and leaks the Runtime Context entirely.
- Existing TUnit conventions use focused `[Test]` methods, controlled in-memory fakes, explicit message/state assertions, and no mocking dependency.
- The existing Unit project already references Client, Server, Runtime, and the controlled rollback fixture; no new test project or package is required.

## Acceptance checklist

- Direct Client profile failure preserves profile and transport cleanup failures and disposes once.
- Direct Client constructor failure preserves constructor and transport cleanup failures and disposes once.
- Dynamic resolver validation preserves validation and resolver cleanup failures and disposes once.
- Server validation preserves validation and Runtime Context cleanup failures and disposes Context once.
- Server constructor failure preserves constructor and Runtime Context cleanup failures and disposes Context once.
- Shared process Catalog/environment fault injection is serialized and removes temporary Manifest entries deterministically.

## Audit guardrails

The full performance-pattern and static source-to-test scans were already completed once during 0.8.4. This pass continued targeted builder ownership review. Caller-owned resources and internal disposal sites without a reachable throwing extension seam were not promoted to independent P2 findings.
