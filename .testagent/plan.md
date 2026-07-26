# 0.8.1 regression-test plan

1. [x] Add mutation probes for authentication scopes and endpoint snapshots, then assert read-only wrappers in generated manifests.
2. [x] Add resolver owned-resource disposal checks for both built-in implementations.
3. [x] Strengthen emitted-source tests so semantic fixed values resolve validating Codecs while ordinary integers remain inline.
4. [x] Capture the existing `Rpc_SumList` allocation/latency baseline, then remove the intermediate array from `BlitListCodec<T>`.
5. [x] Run focused failures before production changes, implement minimal fixes, review assertion gaps, and complete full correctness/performance gates.
