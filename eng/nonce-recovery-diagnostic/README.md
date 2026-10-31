# Nonce recovery: deterministic diagnostic, not a production workaround

The integration fixture verifies malformed acknowledgement nonce rejection, mapping cleanup,
and recovery of the same pending accept. Mapping unlink is not an accept-readiness signal:
cleanup disposes the rejected pipe afterward. The fixture waits for the existing next-iteration
hook, keeps the malformed client open until rejection, flips one nonce byte deterministically,
and verifies a single valid connection with bidirectional bytes. It does not promise that a
client connecting during listener teardown cannot transiently fail.

`run.py` creates three detached worktrees from the exact clean checkout and records the source
patches, actual binary hashes, SDK/runtime, exits, native TUnit reports and artifact hashes.
It never patches the checked-out product. Linux only; no measurements or retries:

1. **positive:** diagnostic-only hooks hold cleanup after mapping unlink but before old pipe
   disposal, and hold a valid client's hello after physical pipe connection. A client deliberately
   queued on the old Unix listener must fail as Unavailable/IOException after release. Waiting
   for the new accept must succeed, on the same accept, with bytes exchanged both directions.
   Also run the strengthened uninstrumented-fixture logic (hooks unset).
2. **remove-nonce-check:** delete only acknowledgement nonce validation in the isolated overlay;
   the fixture must fail specifically because its original accept completed despite the bad nonce.
3. **block-rearm:** hold the next iteration in the isolated overlay until listener disposal;
   the fixture must fail specifically waiting for iteration2, not pass through EOF or a retry.

The two production hooks exist only in the captured diagnostic source overlays. Do not cherry-pick
an overlay into shipping source. A successful diagnostic establishes the stale-listener mechanism
and the fixture's sensitivity, not proof that this was the unique cause of a prior intermittent
failure or a universal availability guarantee. Original failed CI remains retained separately.

Run offline controls with `python3 eng/nonce-recovery-diagnostic/test_run.py`.
The workflow runs the actual proof on an Ubuntu host and uploads every result even on failure.
Local sandbox testing previously failed before the first accept because Unix socket creation was
denied; that is not the stale-listener race and must not be worked around by escalation.

Unexpected exits and timeouts still archive any generated native reports in `finally`, preserving
original errors and expected/actual exits. Stale reports move to a separately named archive before
each process; they cannot satisfy a new case. Offline fake-process tests prove these failure paths
retain evidence without retries. Actual TUnit runtime must be exactly10.0.14, with loaded Pipes and
CoreLib hashes checked against the installed runtime. Official source blob provenance is recorded
separately; matching a source blob does not itself prove a source-to-binary correspondence.
