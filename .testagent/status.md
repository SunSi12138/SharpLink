# 0.8.38 test status

## Evidence status

- Exact baseline is local 0.8.37 commit `576fbe3a9821dca5270ccb5f93e8c1d60d56117c`.
- Real pre-fix service compilation produced generated-manifest `CS1620` for a `ref` dependency and
  `CS0030` for a ref-like dependency; assertion review additionally reproduced `CS9193` for a
  `ref readonly` dependency expression without addressable storage.
- Real pre-fix DTO compilation produced generated-Codec `CS9035` for an ignored C# required member
  and `CS1620` for a selected `ref` constructor.
- Real pre-fix pointer/function-pointer compilation produced ten generated Proxy `CS0214`/`CS0306`
  errors and no SharpLink diagnostic.
- Complete pre-fix Generator was 113 existing passes plus exactly four new failing tests (117
  total). The targeted interceptor proof independently failed because structured cancellation
  recorded `Failed`.
- Post-fix Generator is 117/117 and the targeted interceptor proof is 1/1.
- Post-fix real invalid projects expose only the intended two `SHARPLINK019`, two `SHARPLINK012`,
  and two `SHARPLINK009` diagnostics, with no raw C# compiler failures.
- A real positive-control project covering an `in` DI dependency, `SetsRequiredMembers`, and a
  value-constructor fallback builds with zero warnings and errors.
- Final non-incremental Release is clean; Generator is 117/117, Unit 483/483, and Integration
  241/241.
- Exact 0.8.37/candidate HostApplication build medians are 1.97 -> 1.92 seconds. Intercepted TCP
  RPC medians are 41.848 -> 41.831 microseconds with unchanged 1,584.03 B/op.
- The final 120-second shared-memory Chaos report passed with 878,800 successes, 341,743 expected
  failures, zero unexpected failures, 23 restarts, no Client/Server Errors, a completed drain, and
  all five tracked metrics at zero.
- NativeAOT TCP printed `AOT_SMOKE_PASS transport=tcp`; all seven 0.8.38 packages were created and
  a fresh-cache package-only TCP/shared-memory/static/dynamic functional smoke passed.
- Previous exact-commit 0.8.37 package metadata and fresh-cache all-package/functional smokes pass.
- Consecutive complete audit rounds without a new improvement remain 0/3.

## Current gate

- Review the final diff, commit 0.8.38 locally, and rebuild package metadata against that exact
  commit before resuming the next whole-framework audit round.
