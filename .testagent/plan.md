# 0.8.25 regression-test plan

1. [x] Prove sanitized hint and nested generated-type identities collide.
2. [x] Prove keyword method/parameter symbols emit invalid syntax or semantics.
3. [x] Prove by-ref RPC signatures are accepted without a usable wire model.
4. [x] Prove static abstract RPC methods are accepted without a valid generated implementation.
5. [x] Prove abstract properties/indexers/events leave generated proxies incomplete.
6. [x] Run the complete pre-fix Generator suite and record the exact failure set.
7. [x] Implement only proven fixes and review assertions/pseudo-mutations.
8. [x] Run release, package, documentation, and performance gates; prepare the local 0.8.25 commit.
