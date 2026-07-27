# 0.8.37 test status

## Evidence status

- Exact baseline is local 0.8.36 commit `e4bf5f195aae4d0edb92f48bdcaaee06b8cc6506`.
- An isolated real compilation produces exactly two generated-manifest `CS0122` errors for one
  private nested RPC service.
- An exact-baseline Codec round trip prints
  `RECORD_SLICE_PROVEN runtime=DerivedPayload decoded=BasePayload value=7`.
- Complete pre-fix Generator was 108 existing passes plus exactly five new failures (113 total).
- Post-fix Generator is 113/113; non-incremental Release is clean, Unit is 483/483, and a complete
  Integration rerun is 240/240. One earlier unrelated streaming/Stop timeout did not reproduce.
- Interleaved exact-baseline/candidate HostApplication builds measured median wall time
  2.13 -> 1.89 seconds; runtime hot-path IL is unchanged.
- The final 120-second shared-memory Chaos run passed with 866,582 successes, 337,510 expected
  failures, zero unexpected failures, 23 restarts, zero Client/Server Errors, and five zero metrics.
- NativeAOT TCP smoke printed `AOT_SMOKE_PASS transport=tcp`.
- Seven 0.8.37 packages and a fresh-cache functional PackageSmoke passed before commit.
- A parallel-process final gate reproduced one ARM64 false admission witness because the test used
  a weaker volatile state store than production. The corrected `Interlocked.Exchange` probe passed
  three consecutive complete Unit reruns (483/483 each).
- Consecutive complete audit rounds without a new improvement remain 0/3.

## Current gate

- Commit 0.8.37 locally, verify exact-commit package metadata and fresh-cache package smoke, then
  resume whole-framework audit with the clean-round count still 0/3.
