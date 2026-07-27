# 0.8.22 regression-test research

## Target inventory and evidence candidates

- Generated DTO Boolean members use raw unmanaged reads, accepting non-canonical bytes other than zero and one.
- Generated DTO Rune members bypass the built-in Rune Codec and can materialize invalid Unicode scalar values.
- Generated DTO decimal members bypass the built-in decimal Codec and can materialize invalid flag layouts.
- Generated DTO DateOnly, DateTime, and TimeOnly members bypass their built-in semantic validation.
- Generated DTO DateTimeOffset members use a raw 16-byte struct image, including padding, instead of the canonical validated 10-byte built-in Codec payload.

## Acceptance checklist

- Generated Boolean fields retain their compact one-byte wire representation but accept only zero or one.
- Rune and decimal fields retain fixed wire but use their validated generated readers and surface malformed input as `DataLoss`.
- DateOnly, DateTime, and TimeOnly fields retain fixed wire but use their validated generated readers.
- DateTimeOffset fields retain the 16-byte fixed layout, clear its six padding bytes on write, and reject invalid ticks or offsets.
- Valid generated DTO round trips retain their normal behavior and changed hot paths show no material performance regression.

## Audit guardrails

The automatic performance-pattern scan produced no critical hits; manual review discarded generator string operations and synchronously-read completed tasks as cold-path or deliberate fast-path uses. Blit collections of semantic element types remain a separate audit candidate and are not folded into these five DTO-member findings without their own collection-specific evidence and performance treatment.

## Regression and performance evidence

Against clean 0.8.21 commit `481989c`, the complete pre-fix Integration run contained 236 tests: all 231 existing tests passed and exactly five new behavioral probes failed. The complete pre-fix Generator run contained 84 tests: all 83 existing tests passed and exactly the new emitted-source probe failed. The evidence directly observed acceptance of malformed Boolean, Rune, decimal, DateOnly, DateTime, TimeOnly, and DateTimeOffset bytes, plus propagation of attacker-controlled padding in the raw 16-byte DateTimeOffset layout.

The initial length-delimited Codec implementation was rejected after A/B measured about 66/109 ns for six-field serialize/deserialize versus about 38/35–36 ns at baseline. The final fixed-wire implementation retains 0/80 B/op and measures about 38–39/36–38 ns, an absolute validation cost of about 1–2 ns.
