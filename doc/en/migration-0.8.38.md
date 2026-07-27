# SharpLink 0.8.38 migration guide

Version 0.8.38 does not change valid Protocol v2 framing, route hashes, or payload wire layouts. It replaces invalid generated C# with focused diagnostics and corrects structured cancellation status.

Selected `[RpcService]` constructors can no longer use `ref`/`out`/`ref readonly`, ref-like, pointer, or function-pointer dependencies; use ordinary DI-registerable values instead. Read-only `in` dependencies remain supported. Native DTOs must satisfy every C# required member, including `[RpcIgnore]` members and required fields. Mark a constructor that establishes them with `[SetsRequiredMembers]`, or expose a generated-initializer assignment. Add an ordinary value constructor when a DTO previously offered only `ref`/`out`/`ref readonly` construction.

Pointer/function-pointer RPC payloads now report `SHARPLINK009`; replace them with integer handles, stable DTOs, or another safe representation. Interceptor policies that inspected the old contradictory `Failed` status for `SharpLinkException(Cancelled)` should now handle `SharpLinkInvocationStatus.Cancelled`.
