# TCP window observation follow-up

The diagnostic artifact for `0991a079` (run 36016072122, artifact 10814223986,
SHA256 `54f35129757a437bd25eedc056c14581a8656ec15d80b22be3e78f09aae7d4e3`)
contains a timed-out B3-ready TCP/4096-byte case whose receive count advances
by about 364--365 items per five-second snapshot. Sender and receiver pumps
are idle with no queued/pending frames, and released minus received is 128
items, exactly the 512 KiB connection window. This is slow continuing
progress, not evidence of a permanently lost wakeup. Other A-ready samples
show the same shape. The kernel cause has not yet been established.

The separately compiled diagnostic now invokes read-only `ss -tinmH` for the
single loopback port belonging to the test pair. This records TCP send/receive
queues, advertised window, retransmission and window-limited information next
to the existing counters. It never changes socket options, resets credit,
nudges a wakeup or extends the 45-second case limit. Ordinary JIT and NativeAOT
builds exclude the code, and diagnostic timing remains rejected by validators.
The subprocess has a two-second observation limit and no shell. Failure to
collect telemetry is printed as a diagnostic error, not suppressed or turned
into a performance success. These changes do not themselves fix the slowdown.
