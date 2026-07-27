# 0.8.35 regression-test research

## Proven target inventory

- Dynamic Resolver failures are caught and retried but were emitted as unhandled-background Error `6002`.
- Chaos connected only a Client logger; an injected Server Error exited 0 with `Passed`.
- An explicitly unwritable `--json-output` printed stderr but exited 0.
- Server and Client protocol-terminal paths could await session disposal while retaining an active `PipeReader.ReadResult`; a completion-joining reader deterministically exposed the ordering deadlock.
- Internal Build/session code read only `Options.PerformanceProfile`, causing a defensive deep clone of Protocol, FlowControl, and Compression per access.
- Real TCP restart showed an ordinary reset converted to disconnect/reconnect after first being logged as Client Error.
- Enabling the Server oracle showed rolling-stop response races throwing structured `ConnectionClosed` and being logged as Server Error.

## Pre-fix evidence

- Unit 479 total: all 478 existing tests passed; only `RetriedResolverFailureShouldNotBeAnUnhandledBackgroundError` failed.
- Integration 239 total: all 238 existing tests passed; only `ServerProtocolViolationShouldReleaseItsReadBeforeCompletingTheReader` failed.
- Shared-memory Server injection report exited 0/Passed with one injected Server Error.
- `/dev/null/report.json` emitted `CHAOS_REPORT_WRITE_FAILED` and exited 0.
- TCP rolling restart captured Client `BackgroundLoopUnhandledException` for connection reset.
- Once the Server logger was connected, rolling restart captured `ConnectionClosed: Session is stopping` as Server `BackgroundLoopUnhandledException`.
- Exact `044598c` allocation baseline was 6,536 B per Client Build.

## Acceptance boundary

Handled failures remain observable without weakening genuine background Errors. Both sides of Chaos use bounded aggregate evidence and monotonic counts. Requested report failure must be non-recursive and non-zero. Terminal protocol handling must release the current read before disposal joins completion. Public options keep defensive-copy semantics; only friend-assembly reads use the frozen profile.

## Assertion and pseudo-mutation review

- Changing Resolver Warning back to Error, changing event `6102`, dropping the original exception, or removing retry observability fails the new test.
- Reintroducing `await session.DisposeAsync()` before the loop finally makes the completion-order test observe an outstanding read or time out.
- Removing either Client or Server Error gate makes its injected real-process probe falsely pass; omitting `ServerErrors` loses report evidence.
- Swallowing report-write failure restores exit 0 for the unwritable-path process probe.
- Restoring public `Options` reads restores the exact 368 B/Build allocation delta.
