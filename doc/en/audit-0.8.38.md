# SharpLink 0.8.38 deep audit

Using exact 0.8.37 commit `576fbe3` as the baseline, this batch confirmed five P2 improvements: generated service activation accepted writable by-reference or unboxable dependencies; DTO construction ignored excluded C# required members; DTO constructor selection ignored `RefKind`; pointer/function-pointer payloads bypassed unsupported analysis through the unmanaged fast path and emitted broken artifacts; and structured `Cancelled` interceptor failures recorded contradictory `Failed` status.

Real pre-fix builds produced service-manifest `CS1620`/`CS0030`/`CS9193`, DTO-Codec `CS9035`/`CS1620`, and ten pointer Proxy `CS0214`/`CS0306` errors. The fixes report `SHARPLINK019`, `SHARPLINK012`, or `SHARPLINK009` and suppress only invalid descriptors/artifacts. Ordinary and `in` DI dependencies, `SetsRequiredMembers`, compiler-required fields, and fallback value constructors remain valid. Client and Server contexts now classify both `OperationCanceledException` and `SharpLinkException(Cancelled)` as `Cancelled`.

All 113 existing Generator tests passed before repair while exactly four new tests failed; post-fix Generator is 117/117 and the targeted interceptor test is 1/1. A real positive-control project builds with zero warnings/errors. HostApplication build median improved from 1.97 to 1.92 seconds; intercepted RPC remained 41.848 versus 41.831 microseconds at the same 1,584.03 B/op.

Final gates passed: non-incremental Release, Unit 483/483, Integration 241/241, NativeAOT TCP, and a 120-second shared-memory Chaos run with 878,800 successes, 341,743 expected failures, zero unexpected failures, 23 restarts, no Client/Server Errors, and all five metrics drained to zero. This round found improvements, so the clean-round counter remains 0/3.
