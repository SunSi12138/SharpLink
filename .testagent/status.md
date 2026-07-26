# 0.8.2 test status

## Pre-fix evidence

- Unit: 344 total, exactly 5 failed: fixed shared-connect cancellation, endpoint-cluster timeout cause, unexpected DNS failure propagation, overlong VarUInt32, and invalid UTF-8 error payload.

## Focused result

- Unit: 344/344 after minimal production fixes and handshake-path deduplication.
- DNS assertions cover both Resolve and Watch; protocol assertions cover direct decoder and complete frame validation.
- Three-launch parser A/B: control 39.32 → 40.23 ns; metadata 42.67 → 39.60 ns; both 0 B/op. High baseline variance is documented and no improvement is claimed.

## Final gate

- Versioned Release build passed with 0 warnings and 0 errors.
- Generator 83/83, Unit 344/344, and Integration 227/227 passed.
- `git diff --check` and the final source/assertion review passed.
