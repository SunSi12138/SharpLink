# SharpLink 0.6.9 chaos and soak gate

`SharpLink.ChaosTests` runs mixed unary, client-streaming, server-streaming early-break,
deadline/cancellation, rolling TCP restart, reconnect, and final state-drain checks. A run fails
when it observes data corruption, a non-injected RPC failure, no successful work/restart, a
non-zero final framework gauge, or (for runs of at least six hours) retained-memory growth above
5% over the final six-hour window.

## CI levels

- Pull requests and release tags run a two-minute catastrophic-regression smoke.
- Nightly runs a two-hour mixed chaos soak and retains its JSON report for 14 days.
- The 0.6.9 release gate requires one continuous 24-hour run on a dedicated machine.

GitHub Actions limits each individual step to six hours, including self-hosted runners. Therefore
the continuous release soak is deliberately a host-level command rather than four shorter jobs;
splitting it would invalidate connection-lifetime and final-six-hour retained-memory evidence.

Run the release gate from a clean checkout:

```bash
eng/run-release-soak.sh
```

The default report is `artifacts/chaos/release-24h.json`. The duration, concurrency, restart
interval, and output can be overridden with `SHARPLINK_SOAK_DURATION_SECONDS`,
`SHARPLINK_SOAK_CONCURRENCY`, `SHARPLINK_SOAK_RESTART_SECONDS`, and `SHARPLINK_SOAK_OUTPUT`.

The release report must show zero `UnexpectedFailures` and zero for all final gauges:

- `sharplink.connections.active`
- `sharplink.calls.active`
- `sharplink.requests.pending`
- `sharplink.streams.active`
- `sharplink.send.queue.bytes`

Transport-specific RST/FIN, pipe disposal, TLS/authentication, asymmetric frame limit, bounded
stop, and cancel/response/deadline races remain covered by Unit and Integration tests; the chaos
runner concentrates on cross-feature lifetime behavior under sustained load.
