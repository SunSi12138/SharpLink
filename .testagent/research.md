# 0.8.0 regression-test research

## Scope

- Native built-in Codec decoding in `src/SharpLink.Runtime/Codec`.
- Stream/connection credit batching in `StreamFlowController`.
- RPC method modeling and diagnostics in `SharpLink.Generator`.

## Existing conventions

- Tests use TUnit `[Test]` methods and small local `Ensure` helpers.
- Runtime edge cases live under `test/SharpLink.UnitTests/Runtime`.
- Generator diagnostics and emitted-source assertions live in `RpcAnalyzerTests`.
- Final validation uses the Microsoft Testing Platform commands documented in `CONTRIBUTING.md`.

## Acceptance checklist

- [x] Fixed and variable native Codecs reject trailing bytes identically for contiguous and segmented input.
- [x] Boolean values and nullable presence markers reject non-canonical wire bytes.
- [x] Reaching the connection-credit batching threshold does not leave consumed credit stranded on another open stream.
- [x] Adapter-selected unmanaged request values are length-delimited through their selected Codec rather than native-blitted.
- [x] A marked RPC contract models methods inherited from an ordinary base interface.
- [x] Narrow tests, full Release build, generator/unit/integration suites, and targeted performance evidence pass.
