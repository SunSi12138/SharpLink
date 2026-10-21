# Public evidence scope

This branch publishes the originally planned ordinary test/model source and
research narratives. Production `src` is byte-identical to dev
`13928a35df4bff3cd99b14bd1df131aa3eb3bcdb`. The separate public-API tooling
correction is draft [#928](https://github.com/SunSi12138/SharpLink/pull/928).

## Reproducible from this branch

- `test/SharpLink.IntegrationTests/ColdSessionFenceResearchTests*.cs`: 13 real TCP
  same-physical-session characterizations, including expected false-zero behavior.
- `test/SharpLink.UnitTests/Runtime/ColdQuiescenceServerOwnershipAuditTests.cs`:
  three generated-pump/custom-child characterizations.
- `queued-replacement-cancellation.patch` plus `run-queue-repro.sh`: isolated
  positive control and exact expected assertion failure for the existing queue bug.
  A green research step means the bug was reproduced, not fixed.
- `model/model.py` and `model/fence_model.py`: bounded conditional ordering
  checkers and 14 negative mutants. Run in a disposable output directory; they
  write their own JSON results there.

The evidence workflow preserves canonical Fast/Extended commands and budgets,
checks no production-source difference, runs these models and queue reproduction,
and uploads **fresh CI-generated** queue logs and model results. It does not
upload pre-existing local archives or replay an ownership prototype. Browser/WASM
is intentionally outside the Extended scope.

## Local-only evidence and access limitation

The initial local raw logs, bad samples, full ownership prototype source/IL/JIT
and reproduction bundle were sealed locally. Public archive upload was not authorized. Archive uploads stopped. The denied payload was not unpacked into plaintext or rerouted through CI.
Consequently the local ownership/inbound comparison claims in the reports are
not independently replayable from this public branch. They are not labelled CI
results or production acceptance.

Preserved archive identities (SHA-256), for provenance only:

- Queue evidence: `9be7ce1808f4cad2d67a8f889501d62c184c2dc55c02653af5f195d82b63d2b6`.
- TCP fence historical runs: `0d78a99af01b2620ca74a8915a42dca67175aa9292506959c22cfd7b1e01ed76`.
- Bounded model historical runs: `95c876ccbc69a8091d8fd8de87a3d3ea10b3031958ce0d40998f2c537586edee`.
- Built-in inbound controls: `d50280b7fa9fc05e56eb977f22a0df76f174b01500b57c5a904e7a416a528125`.
- Independent ownership review: `36f4a69ae231f97c0071f58b6f52d05e72906e64ea8bcad66caf7484c58b41aa`.
- Complete source-only ownership replay: `66adf52799e552f7c5c3235bee4a66a6cb05dc8502bb59e54430b0e58dd6ac5b`.
- Restricted local baseline logs: `a4220d4b1232e01073134ae753537dc2ee9aa272e459fe2bd072ed0912dc9122`.

Those hashes are not download links. No remote archival availability is claimed.
