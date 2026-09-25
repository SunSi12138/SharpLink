# Independent TCP receive-buffer control

The failed A-ready warmup at `13237a20` now has kernel evidence, not merely
managed waiting tasks. Run `36019812964`, artifact `10815718972`, SHA256
`bc80438675373437458dcd369d3b7159d64a3c8ffaa1d02a8ccd93c0d92c691e`:

```
received 10728; returned 43941888; writer released 10856 frames
sender/receiver SendPump pending frames 0, queued bytes 0
sender Send-Q=524848; notsent=524848; snd_wnd=52224; mss=62464
busy=44991ms; rwnd_limited=44986ms (100.0%); rto=201
receiver Recv-Q=0; r0; rb52236; rcv_space=51215; rcv_ssthresh=51215
```

Across the last two five-second snapshots, bytes acknowledged increase by
1,253,376 (24 advertised windows). Delivery is progressing very slowly while
being receive-window limited; this does not establish the reason that the
receive buffer/window shrank or a particular kernel defect. No permanent
credit-owner deadlock was demonstrated. This captured failure is A-ready.

The new workflow is a **diagnostic control, not a fix or performance report**.
It preserves every ordinary default-setting JIT/NativeAOT matrix. A hash-checked
patch to the temporary transport pair selects either the unchanged default or
an explicit `SocketTransportOptions.ReceiveBufferBytes=262144` for both client
and accepted server sockets. A and B3 always use identical options. No default
shipping socket settings, protocol windows, buffer pool, NoDelay option, item
size, flush policy, or 45-second case limit is changed. Actual kernel allocation
may differ from the requested socket option; retained ss output is authoritative.
Explicit receive-buffer configuration may also change autotuning, so a positive
control alone is not proof of a specific kernel patch or a production remedy.

The patch refuses source drift and contains a compiler error unless the host is
built with `SHARPLINK_READY_WRITER_DIAGNOSTIC`. Existing diagnostic markers reject
all its rows from normal timing acceptance. The workflow records uname, read-only
TCP sysctls, SDK, source and overlay hashes, and keeps existing live ss snapshots.
No sysctl, interface, MTU, or privileged setting is modified.

Run four independent child processes in default/configured/configured/default
order, alternating the existing mode order. Each keeps c128, 4096-byte items,
128 items/stream, 12 rounds, 512 KiB connection window, 8 KiB stream window,
16 producer slots, count-only preparation and 16 KiB flush. Every process is
attempted once; nonzero and timed-out defaults do not prevent the other profile
from being collected, but the job still fails overall. All logs, incomplete
reports and exits are retained. There is no retry-until-green or relaxed timeout.
Self-checks exercise both profiles before the matrix, including real TCP capture.

Interpret alongside the preserved default failure: whether a configured profile
avoids a below-MSS advertised window, whether rwnd-limited time changes, and
whether DATA/credit still settle. Never label a socket-profile improvement as B3
lock-removal speedup. This is not the final production choice. Full lifecycle,
FIFO, duplicate/clamped credit and RPC acceptance remain open; #735 stays open
and #742 stays Draft.
