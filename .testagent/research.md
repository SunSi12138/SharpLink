# 0.8.38 regression-test research

## Candidate inventory

- The generated service activator treats every selected constructor dependency as an ordinary
  value that can round-trip through `IServiceProvider.GetService`. Legal constructors with
  `ref`/`out`/`ref readonly`, ref-like, pointer, or function-pointer parameters instead produce
  generated calls with missing storage/modifiers, illegal boxing/casts, or an unsafe-context
  requirement.
- Native DTO construction considers only serialized members. A C# `required` member excluded by
  `[RpcIgnore]` is consequently absent from the generated object initializer, so the generated
  Codec leaks compiler `CS9035` instead of a SharpLink construction diagnostic.
- DTO constructor selection compares parameter names and types but ignores `RefKind`. A legal DTO
  constructor with a `ref` parameter is selected, while the emitter produces a value argument and
  leaks compiler `CS1620`.
- DTO analysis diagnoses pointer and function-pointer payloads, but contract artifact suppression
  checks only by-reference and ref-like signatures. Proxy/Stub source is still emitted for these
  intrinsically unsupported payloads and requires invalid generic/unsafe shapes.
- Client and server interceptor tracking classifies only `OperationCanceledException` as
  cancellation. A structured `SharpLinkException` whose code is `Cancelled` records status
  `Failed` while simultaneously recording error code `Cancelled`, contradicting the public
  invocation context contract.

## Acceptance boundary

- Emit `SHARPLINK019` and suppress only the service descriptor when its selected constructor
  cannot be represented by the generated service-provider activator. Ordinary value/reference
  dependencies retain byte-for-byte generated activation.
- Emit `SHARPLINK012` and suppress the DTO Codec when a required member would remain unsatisfied,
  while accepting constructors annotated with `SetsRequiredMembersAttribute`.
- Exclude DTO constructors that require `ref`, `out`, or `ref readonly` storage from the generated
  construction plan and select another valid constructor when one exists; ordinary `in` remains
  valid.
- Treat pointer and function-pointer RPC payloads as artifact-blocking invalid shapes after their
  existing `SHARPLINK009` diagnostic; no new public diagnostic ID or wire change is needed.
- Record `SharpLinkInvocationStatus.Cancelled` for either `OperationCanceledException` or a
  `SharpLinkException` with `SharpLinkErrorCode.Cancelled` on both client and server paths.

## Planned evidence

- Compile real generated output for service constructors using `ref` and `Span<T>` dependencies,
  preserve the raw compiler failures, then require two `SHARPLINK019` diagnostics and no service
  descriptors.
- Compile a DTO with an ignored required property, preserve `CS9035`, then require
  `SHARPLINK012`; add a positive `SetsRequiredMembers` control.
- Compile a get-only DTO restored through a `ref` constructor, preserve `CS1620`, then require
  `SHARPLINK012`; add a value-constructor control.
- Generate an unsafe pointer/function-pointer contract, preserve the broken Proxy/Stub evidence,
  and assert both artifact families are suppressed.
- Exercise client and server interceptors that throw structured `Cancelled` failures and assert
  status, error code, exception identity, and terminal result.

## Assertion and pseudo-mutation review

- Removing any constructor-shape predicate must restore either the raw generated compiler errors
  or an emitted invalid service descriptor; ordinary dependencies are a positive control.
- Ignoring non-serialized required members must restore `CS9035`; treating every ignored member as
  invalid must break the `SetsRequiredMembers` positive control.
- Ignoring DTO constructor `RefKind` must restore `CS1620`; rejecting the DTO rather than only the
  bad constructor must break fallback-constructor selection.
- Reporting pointer DTO diagnostics without feeding the shape into contract suppression must leave
  the exact Proxy/Stub absence assertion failing.
- Mapping cancellation from exception type alone must leave both structured-cancellation status
  assertions failing even though their error-code assertions pass.
