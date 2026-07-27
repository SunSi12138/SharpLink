# 0.8.35 test status

## Evidence status

- Five version-advancing P2 findings and two follow-up P2 findings have pre-fix runtime evidence.
- Post-fix Unit is 479/479 and Integration is 239/239.
- Compiled shared-memory and TCP rolling-restart probes pass with zero unexpected failures and zero Client/Server Errors.
- Server injected-error gate exits 2; unwritable requested-report gate exits 6.
- Three-process A/B allocation is 6,536 -> 6,168 B/Build with no latency regression.

## Remaining final gate

- Pack all seven packages, verify exact embedded commit after commit, and run fresh-cache package smoke.
- Final non-incremental build is 0 warnings/errors; Generator 108/108, Unit 479/479, Integration 239/239.
- Final 120-second Chaos passed with 818,793 successes, 302,335 expected failures, 11 restarts, no unexpected failures or Client/Server Errors, successful drain, and five zero active metrics.
- Client/Server Error injection and unwritable-report exit probes passed; NativeAOT printed `AOT_SMOKE_PASS transport=tcp`.
- Consecutive complete audit rounds without a new improvement remain 0/3.
