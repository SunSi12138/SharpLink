# 0.8.26 regression-test research

## Target inventory and evidence candidates

- `[Oneway]` accepts payload-returning Task/ValueTask and streaming returns, producing uncompilable or descriptor/invoker-mismatched code.
- User parameters named `__request` or `__streams` collide with generated Proxy locals.
- DTOs with public members differing only by case crash constructor analysis in `ToDictionary(StringComparer.OrdinalIgnoreCase)` instead of reporting a stable construction diagnostic.
- Generated dictionary readers pass a null reference key to `Dictionary.TryAdd`, leaking raw `ArgumentNullException` instead of structured `DataLoss`.
- Non-public default interface helper methods are modeled as RPC routes, producing inaccessible Stub calls and unintended Manifest entries.

## Acceptance checklist

- Oneway routes accept only non-generic Task/ValueTask returns and never stream responses.
- Generated method locals are deterministically unique against every user parameter.
- Case-distinct DTO members remain supported; exact constructor-name matches win, while ambiguous case-insensitive fallback produces normal constructor analysis rather than a generator crash.
- Null dictionary keys report generated `DataLoss` before entering BCL collection code.
- Non-public default helper methods are ignored; non-public abstract methods are diagnosed instead of leaving an incomplete proxy.
- Valid paths show no material Generator or runtime regression.

## Audit guardrails

The proposed collection-count change was explicitly disproved: nested generated items use fixed UInt32 length prefixes, so the existing four-byte structural lower bound is correct. That probe and code change were removed and do not count. Direct string wire and unmanaged ABI/padding remain separately queued because changing them requires a versioned design.
