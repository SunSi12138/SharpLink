# 0.8.11 regression-test research

## Verified candidates

- Client `RegisterAssembly` lets generated Codec Adapter cleanup replace a structured Codec conflict.
- Server `RegisterAssembly` has the same diagnostic loss and can stop candidate-service cleanup after the first failure.
- Client `ReplaceAssemblyAsync` lets generated Codec Adapter cleanup replace its structured preparation conflict.
- Server `ReplaceAssemblyAsync` has the same diagnostic loss across candidate services and generated Codec ownership.
- `SharpLinkServerBuilder.Build` performs performance-profile binding before its cleanup boundary, leaking the Runtime Context and losing its cleanup failure when binding fails.

## Audit guardrails

The full performance-pattern and static source-to-test scans were already completed once during 0.8.4. This pass continued targeted transaction-ownership review. Internal admission/service disposal sites with one serialized Server owner were rejected as independent P2 findings because no second reachable concurrent disposer was found.
