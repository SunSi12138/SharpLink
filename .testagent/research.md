# 0.8.37 regression-test research

## Candidate inventory

- Service and explicitly rooted DTO analysis do not verify that the annotated type and every
  containing type are accessible from the sibling `SharpLink.Generated` namespace. Private,
  protected, private-protected, and file-local declarations therefore leak raw C# accessibility
  errors from generated code.
- DTO member analysis stores an already escaped keyword (for example `@class`) and then embeds it
  inside local names such as `local_@class` and `seen_@class`, which are invalid identifiers.
- Native DTO generation permits unsealed record classes even though it rejects other unsealed
  classes. Passing a derived record through the base record Codec silently serializes only base
  state and deserializes a base instance.
- Ref-like structs are treated as unmanaged built-ins, while generated Proxy/Stub paths require
  them as ordinary generic arguments and fields. The result is broken generated C# rather than a
  SharpLink diagnostic.
- Static abstract operators and conversions are excluded from RPC route discovery and from the
  unsupported-member check. A generated Proxy cannot satisfy those inherited interface members.
- The admission/drain race test uses a volatile state store while production transitions through
  `Interlocked.Exchange`. Under parallel process load ARM64 can observe a store-buffering outcome
  that is impossible at the production full-fence boundary, creating a false release failure.

## Acceptance boundary

- Emit `SHARPLINK018` for inaccessible service types and `SHARPLINK009` for inaccessible explicit
  DTO roots, while allowing public, internal, and protected-internal declarations that sibling
  generated code can access in the same assembly.
- Keep escaped syntax only for member access and use raw symbol names when composing generated
  identifiers.
- Require native generated record classes to be sealed so the declared wire schema cannot silently
  slice runtime-derived state.
- Emit `SHARPLINK009` for ref-like DTOs and suppress the affected contract Proxy/Stub.
- Emit `SHARPLINK054` for abstract operators/conversions and suppress the affected contract
  Proxy/Stub; default/static non-abstract interface helpers remain allowed.
- Preserve the admission race schedule scan but make its state transition atomic and full-fence,
  exactly like production `TransitionTo`.

## Planned evidence

- Compile an isolated private service to preserve the two raw `CS0122` failures from the generated
  manifest, then cover service and DTO diagnostics in the Generator suite.
- Assert generated source never contains `local_@class` or `seen_@class` and still accesses
  `value.@class` correctly.
- Run an exact-baseline Codec round trip to prove `DerivedPayload` decodes as `BasePayload`, then
  reject an unsealed base record used as an explicit native DTO root.
- Reject a ref struct used by an RPC contract and assert no Proxy is emitted.
- Reject a static abstract operator and assert no Proxy is emitted.
- Reproduce the load-only admission false positive, replace the weak test transition, and run
  consecutive complete Unit suites.

## Assertion and pseudo-mutation review

- Removing either containing-type traversal or one rejected accessibility kind must restore raw
  generated accessibility failures; allowed same-assembly accessibility gets a positive control.
- Reusing escaped member syntax for local names must fail the two exact invalid-name assertions.
- Restoring the record exception must remove the expected diagnostic and re-enable silent slicing.
- Checking ref-like roots only after unmanaged short-circuit, or failing to suppress contract
  generation, must fail separate assertions.
- Ignoring non-ordinary abstract methods must restore both the missing diagnostic and broken Proxy.
- Reverting the test setter to volatile store must restore the ARM64-only false witness under
  process-level load; the product admission implementation remains unchanged.
