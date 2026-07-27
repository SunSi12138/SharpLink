# 0.8.24 regression-test plan

1. [x] Prove invalid timeout constants are accepted or generate broken descriptor code.
2. [x] Prove non-positive union tags enter the contract manifest.
3. [x] Prove invalid union case relationships/shapes and duplicate type mappings enter the manifest.
4. [x] Prove an explicit empty contract-assembly filter falls back to unrelated reference scanning.
5. [x] Prove generated manifests report stale generator provenance.
6. [x] Run the complete pre-fix Generator suite and record the exact failure set.
7. [x] Implement only proven fixes and review assertions/pseudo-mutations.
8. [x] Run complete release, package, documentation, and performance gates; prepare the local 0.8.24 commit.
