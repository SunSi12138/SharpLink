# 0.8.6 test status

## Pre-fix evidence

- Unit 366 total: both new cleanup probes failed before production changes. Stream writer completion skipped owned-stream disposal; RpcSession pipeline completion skipped transport disposal.
- Unit 367 total: connection-service cleanup reported success despite two disposal failures; both services were nevertheless reached.
- Unit 368 total: server-wide cleanup retained only the first singleton failure and discarded the later singleton and provider failures.
- Unit 369 total: a listener failure after HostedService startup left the Generic Host running; the stop-notification probe timed out after two seconds.
- Both failures were recorded before their production fixes.

## Final gate

- Verified P2-or-higher improvements: 5/5 executable proofs recorded (Stream transport cleanup isolation; RpcSession cleanup isolation/shared terminal outcome; supervised connection-service cleanup diagnostics; complete server-wide service cleanup diagnostics; Hosted Server run supervision).
- Generator 83/83, Unit 369/369, Integration 228/228, Release build with 0 warnings/errors, package generation/analyzer verification, and package restore/run smoke passed.
- Normal RpcSession disposal measured 950.9 → 955.8 ns with overlapping 99.9% confidence intervals and unchanged 17.5 KB allocation.
- Version 0.8.6 and Chinese/English audit, migration, performance, README, and changelog documentation are complete.
