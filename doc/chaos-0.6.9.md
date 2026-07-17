# SharpLink 0.6.9 chaos and soak gate

`SharpLink.ChaosTests` runs mixed unary, client-streaming, server-streaming early-break,
deadline/cancellation, rolling TCP restart, reconnect, and final state-drain checks. A run fails
when it observes data corruption, a non-injected RPC failure, no successful work/restart, a
non-zero final framework gauge, or (for runs of at least six hours) retained-memory growth above
5% over the final six-hour window.

Fault attribution is generation-based rather than a fixed time window. A call is an injected
failure only when it started during a restart generation or overlapped a generation change. After
the listener is recreated, the runner requires a successful probe RPC before closing that fault
generation. Recovery taking longer than 20 seconds is recorded as an unexpected failure. This
keeps slow CI runners honest without misclassifying a still-recovering connection as healthy.

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

`MaxRecoveryMilliseconds` reports the slowest complete stop, listener recreation, reconnect,
handshake, and successful probe cycle. The two-minute 0.6.9 candidate run completed 10 rolling
restarts with a maximum recovery of 6,559 ms and zero unexpected failures.

Transport-specific RST/FIN, pipe disposal, TLS/authentication, asymmetric frame limit, bounded
stop, and cancel/response/deadline races remain covered by Unit and Integration tests; the chaos
runner concentrates on cross-feature lifetime behavior under sustained load.
