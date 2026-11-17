# 06 — Performance

## Decision

```text
S performance: NOT_RUN
N performance: NOT_IMPLEMENTED
Q performance: NOT_IMPLEMENTED
H performance: NOT_IMPLEMENTED
C-NEG performance: FORBIDDEN
A/A noise validation: NOT_RUN
ABBA/BAAB paired comparison: NOT_RUN
Cross-arm conclusion: INCONCLUSIVE
```

This is not a missing appendix; it is the required stop. #998 requires the core correctness set and then the full F01–F24 matrix before performance. S passes the narrow TCP/Replace core set at `4f927b40`, but the full matrix is not implemented. No eligible N candidate exists. The C-NEG arm is intentionally invalid and can never be a performance arm.

Note: at test commit `4f927b40`, the narrow TCP/Replace core set (F03/F05/F06/F07/F09/F10/F18, including a held-duplex F18b) passes; at `33fb48c2`, F08 passes; at `948fb44d`, F04 fails closed; at `7d21b89a`, F11 passes three sequential replacement cycles; at `29cf85de`, F20 simulates notice loss; at `9ad6fe74`, F24 quiesces public counters after five cycles and Stop. That does **not** satisfy the full F01–F24 gate: F01/F02, F11's late/out-of-order notification, F20 duplicate/reorder, F12–F17/F19 and F21–F23 remain not implemented or not run. Therefore performance is still correctly `NOT_RUN`.

## What was not run, and why

| Preregistered item | Status | Reason |
|---|---|---|
| Same-machine A/A with 4 pairs and the >3% median / >5% P99 / >3% CPU noise gates | NOT_RUN | no eligible candidate |
| ABBA/BAAB 8 pairs | NOT_RUN | no eligible candidate |
| TCP + SharedMemory | NOT_RUN | SharedMemory is not executable on this host: mapping creation/access is denied |
| Unary / OneWay / Streaming | NOT_RUN | no eligible candidate; streaming correctness is also incomplete |
| concurrency 1/8 | NOT_RUN | no eligible candidate |
| stable vs hot replacement | NOT_RUN | S replacement is not correctness-qualified |
| CPU/op, B/op, P50/P99/P99.9 | NOT_RUN | no samples |
| first handshake / Bind cost | NOT_RUN | no N implementation; S handshake not benchmarked |
| service-update downtime | NOT_RUN | no eligible candidate |
| unrelated B impact | NOT_RUN | only a functional collateral observation (F09), not a latency measurement |
| physical resource-release cost | NOT_RUN | no eligible candidate |
| allocation gate negative control | NOT_RUN | did not modify the gate or run a release allocation benchmark |

## Samples

There are **zero** formal performance samples. There are no deleted slow samples, because no formal performance run was started. The only timing values in this packet are functional-test monotonic events, not benchmark measurements.

## Environment capability limits relevant to performance

- The Windows host cannot create the SharedMemory mapping under the current sandbox (`UnauthorizedAccessException`); 32 SharedMemory integration tests failed for this reason.
- TLS PFX import is denied (`CryptographicException: 拒绝访问`).
- UDS bind/delete is denied.
- Therefore even a later eligible candidate could not be compared across TCP and SharedMemory on this host, and #998's cross-transport matrix would be `INCONCLUSIVE` here.

## What would be required to make a valid comparison

1. An N candidate that actually implements wire 6, Bind/Invalidated/Release/RequestAccepted, exact-token admission, group cut and the physical owner obligations, plus a correctness result on the same frozen matrix.
2. An S candidate that passes all F01–F24, including F18b held-duplex and control-frame loss/reorder, without breaking existing dynamic-registration behavior.
3. A host/runner where SharedMemory, TCP and TLS/UDS prerequisites are actually available, or an explicit capability-limited matrix that is accepted before measurement.
4. A preregistered A/A run and then 8 paired ABBA/BAAB runs with the #998 sample sizes and 10 s/20 s warmup/measurement windows.
5. The telemetry hooks disabled during formal timing and binary SHA-256 recorded for every arm.

None of these conditions was met. The correct result is **INCONCLUSIVE**, not “no regression” and not “S is faster”.
