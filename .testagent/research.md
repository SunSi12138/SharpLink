# 0.8.4 regression-test research

## Verified candidates

- `RpcCodecProvider.GetCodec<T>` can resume after a generated registration is published and cache/return a codec from the prior registration. The generated-factory and fallback-resolver branches both lack a post-resolution generation check.
- The same blocking resolution paths can resume after `RpcCodecProvider.Dispose`, repopulate the cleared cache, and return a codec from an already disposed runtime context.
- `PreAdmissionStreamDispatcher.Attach` synchronously waits for an incomplete dispatcher `ValueTask`. A bounded consumer cannot start until registration returns, so buffered replay can deadlock admission.
- `RequestDispatchers.CompleteAll` invokes arbitrary dispatcher completion and lease callbacks while holding the per-request stream lock. Reentrant dispatcher code can deadlock the request registry.
- A fifth P2-or-higher item must be independently reproduced before the 0.8.4 version gate advances; adjacent symptoms from one invariant will not be double-counted.

## Performance scan checklist

The complete source scan found: `IndexOf` literal without explicit comparison 0; `Substring` 7; `StartsWith`/`EndsWith` literal without comparison 1; `Contains` literal without comparison 3; `async void` 0; sync-over-async signals 13; parameterless case conversion 0; three-call `Replace` chains 0; `params` signatures 2; LINQ-over-char 0; static readonly mutable dictionaries 0; static readonly frozen dictionaries 0; `new List` 44; `new Dictionary` 39; current-culture comparers 0; LINQ operation signals 139; `new HttpClient` 0; `new JsonSerializerOptions` 0; regex signals 0; unsealed classes 19; sealed classes 237.

The string and LINQ hits are predominantly generator/build-time or setup code. The actionable sync-over-async hit is the pre-admission replay path above. The static source-to-test pairing scan was run once; its 401 unpaired count is inflated by an ignored baseline source copy under `artifacts/performance/0.8.2-parser-ab/baseline-src`, so it is only a navigation heuristic, not coverage evidence.
