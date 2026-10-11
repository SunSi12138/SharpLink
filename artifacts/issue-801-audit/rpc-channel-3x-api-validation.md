# Issue #801 — 3.0 IRpcChannel stream-sink public API simplification: executed proof

## Motivation

On `dev` @ `eb99fe887cf2129d9b88441245ca0a4a6406b6c2`, the public [`IRpcChannel`](https://github.com/SunSi12138/SharpLink/blob/dev/src/SharpLink.Abstractions/IRpcChannel.cs) inherits `IRpcClientStreamSink`, but `SharpLinkClient.SendClientStreamAsync<T>` always creates a failed Task with an error instructing the caller to use the *connection-bound* sink. Both the module channel and metadata channel expose equivalent forwarding methods. The actual generated client stream writer uses the selected [`ClientConnection : IRpcClientStreamSink`](https://github.com/SunSi12138/SharpLink/blob/dev/src/SharpLink.Client/ClientConnection.cs).

## Exact temporary change

[Experiment code](https://github.com/SunSi12138/SharpLink/blob/audit/issue-801-dev-3x-deadcode/eng/issue801/probe-rpc-channel-sink.py) made only in the ephemeral GitHub Actions runner:

1. Change `IRpcChannel : IRpcClientStreamSink` to `IRpcChannel`.
2. Remove always-failing `SharpLinkClient.SendClientStreamAsync<T>`.
3. Remove `SharpLinkModuleRpcChannel.SendClientStreamAsync<T>` forwarding to failure.
4. Remove `SharpLinkMetadataRpcChannel.SendClientStreamAsync<T>` forwarding to failure.

The `IRpcClientStreamSink` type, `ClientConnection` implementation and Source Generator's connection-bound streaming writer are unchanged.

## Actual results

- [Successful GitHub Actions #38103628226](https://github.com/SunSi12138/SharpLink/actions/runs/38103628226)
- [All logs and source diff artifact](https://github.com/SunSi12138/SharpLink/actions/runs/38103628226/artifacts/11688667650)
- **Complete Release solution build: PASS (0 warnings, 0 errors).**
- **UnitTests: PASS 2,018 / 2,018.**
- **Generator Tests: PASS 269 / 269.**
- **NativeAOT Linux x64: PASS** for out-of-process shared memory, TCP, SharpPack adapter, pre-credit TCP and shared-memory streams and referenced generated service.

## Breaking-change policy

- This is **not safe to backport to 2.x without a compatibility plan**. Removing the inherited interface changes the public type relationship and consumers that cast `IRpcChannel` to `IRpcClientStreamSink` may break.
- The experiment validates only the repository's compiled and generated workloads, not arbitrary third-party channel implementations or binary compatibility. Retain the separate public `IRpcClientStreamSink` contract for typed streaming.
- If approved for 3.0, update `eng/public-api/2.0.0` API comparison/diff strategy for the new major, 3.0 migration docs, and any user-facing examples/SDK assumptions.
- The test run reset the temporary checkout; no production source file was pushed to GitHub, and `dev` remains unchanged.
