# SDK security validation scope

This validation branch raises the repository SDK floor to 10.0.114 within the existing feature band. It does not publish packages, deploy applications, or certify that deployed runtimes were updated.

The independent validation workflow runs the PR Fast build/test commands and PR Package Smoke commands on the patched SDK. It verifies SDK 10.0.114 and bundled runtime 10.0.14 before restoring or building. Framework-dependent consumers retain patch roll-forward so their net10.0 baseline can run on the installed patched runtime; bundled-runtime verification is not a claim of exact runtime identity for every consumer. Raw process logs remain on the disposable runner; only bounded status identifiers and diagnostic codes are emitted. No artifacts or caches are uploaded. These jobs are not the full release gate or the managed-allocation gate.

## Servicing baseline binding

The production servicing workflow resolves its baseline from repository `global.json`, and its aggregator requires the producer SDK to equal that value. Consequently this change makes 10.0.114 the current baseline. Existing exact runtime identity checks, all four baseline/latest producer-consumer edges, fixture registries, and wire-hash and semantic checks remain unchanged.

Historical evidence created with SDK 10.0.102 remains historical evidence. It is neither relabeled nor treated as evidence for the new baseline. The independent validation workflow does not invoke the existing servicing workflow, whose artifact steps upload raw corpora. A fresh current-baseline servicing run and any historical-to-patched bridge require their own reviewed execution and output policy. No old SDK is installed or executed by this validation, and no historical bridge result is claimed.

Mobile, WebAssembly, cross-platform servicing, and release readiness are outside this validation's scope.
