# 0.8.10 regression-test research

## Verified candidates

- Fixed-endpoint Client construction swallows transport cleanup failure after later option/client validation fails.
- Endpoint transport profile binding swallows factory cleanup failure and reports only the bind error.
- `RpcGeneratedManifestRegistration.Create` swallows candidate Adapter Scope cleanup failures after a later factory/Scope failure.
- `SharpLinkRuntimeContext` construction swallows earlier prepared Manifest cleanup failures after a later Manifest fails.
- `SharpClientBuilder.BuildCore` lets Runtime Context cleanup replace the original Client build failure.

## Audit guardrails

The full performance-pattern and static source-to-test scans were already completed once during 0.8.4. This pass continues targeted ownership and concurrency review without rerunning identical heuristics.
