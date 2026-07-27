# SharpLink 0.8.27 deep audit

Chinese: [`../audit-0.8.27.md`](../audit-0.8.27.md)

Against 0.8.26 commit `1d8325e`, this batch confirmed five P2 improvements: a payload-bearing response bypassed its Codec and silently produced `default(T)` when empty; a consumer enumeration token replaced the response stream call/lease token; writer Return racing pool Dispose could place an ArrayPool-backed writer in a detached queue; a hosted Server run loop completing successfully after startup did not stop the Host; and a failed anonymous-pipe connection reset the one-shot gate and permitted reuse of inherited handles that may already have been consumed or closed.

The complete pre-fix Unit run contained 454 tests: all 449 existing tests passed and exactly five new probes failed. Evidence captured silent `default(int)`, call cancellation masked for 250 ms, one detached writer left by a 15 ms race probe, no Host stop request within 500 ms after Server exit, and a second anonymous-pipe attempt leaking as `UnauthorizedAccessException` instead of being rejected by the one-shot guard.

Pending operations now explicitly record whether a response carries a business payload. Required payloads always reach the Codec even when empty; payload-less acknowledgements accept only empty input. The stream dispatcher retains one primary token and adds a second registration only when two distinct cancellable tokens exist, keeping the ordinary single-token path allocation-free. Writer Return rechecks pool ownership after enqueue so either racing owner drains the detached queue. The Hosted Service uses its own stop flag to distinguish shutdown from unexpected successful exit. The anonymous-pipe gate remains closed once the first attempt begins.

The strengthened Unit suite passes 454/454. The exact final tree also passed a non-incremental Release build with 0 warnings/errors, Generator 101/101, Integration 237/237, seven-package pack, and fresh-cache package smoke. All three related 15-sample hot-path A/B comparisons retained their allocation profiles and passed latency gates. See [`performance-0.8.27.md`](../performance-0.8.27.md) and [`migration-0.8.27.md`](../migration-0.8.27.md).
