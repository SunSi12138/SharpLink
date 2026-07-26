# 0.8.8 regression-test research

## Verified candidates

- `AnonymousPipeTransportConnection.DisposeCoreAsync` can skip the input pipe after writer, reader, or output cleanup fails.
- `SharedMemoryTransportConnection.DisposeCoreAsync` can skip its mapping after control-channel cleanup fails.
- `SharpLinkServer.ReleaseModuleAsync` reaches remaining services and manifest cleanup but exposes only the first failure.
- `SharpLinkServer.ReleaseDrainedDynamicModulesAsync` reaches remaining modules but exposes only the first module failure.
- `SharpLinkServer.DisposeRegisteredServicesAsync` reaches later ownership boundaries but exposes only the first dynamic/static/admission/runtime failure.

## Audit guardrails

The full performance-pattern and static source-to-test scans were already completed once during 0.8.4. This pass continues targeted ownership and concurrency review without rerunning identical heuristics.
