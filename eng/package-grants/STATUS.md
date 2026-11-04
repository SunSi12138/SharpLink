# Historical status snapshot before Revision 2

Snapshot: 2026-11-04 21:13 UTC. This document is not a declaration that a later workflow run passed.

Production target remains `8a13b12e799bb70a989eacac0c1b4cea22ab80ae`, source subtree `bfc211191d91b5a77c6a8f0085a0c00a459c5583`. Tools are on independent validation branch `validation/805-package-grants-20261104`; repaired source snapshot is `9a04c70ac8c41a7cdbfcd2e49c2b18143b468d79`. The workflow must record its own actual triggering tools commit separately.

## First hosted run failed

Run [41912862444](https://github.com/SunSi12138/SharpLink/actions/runs/41912862444), job 126864696255, tools commit `83a373bc488acee36195f0e41969ec653e9b5aa5`: 10 producer restores/builds, nine package packs and existing package verification completed. Producer and isolated reader selected SDK 10.0.114, host 10.0.14. The reader-self-tests command exited zero without timeout, but process ownership reported one cleanup error, so the job correctly failed. Actual package assembly inventory was not run. No grants pass may be inferred. See [FIRST-RUN.json](FIRST-RUN.json).

Only cleanup error count was retained, not its process identity or raw receipt. A persistent compiler/build server is a candidate explanation, not a confirmed cause.

## Reviewed Revision 2

All four fixture builds now explicitly use `UseSharedCompilation=false`, `-m:1`, `-nr:false`; owner failure conditions are unchanged. Diagnostics add fixed known-cleanup classification counts and remaining-owner count, never stdout/stderr or exception text. Eleven runner tests pass locally. Scanner source remains unchanged and 44 metadata checks pass. Fresh local run_owned E2E on SDK 10.0.400 parsed the returned stdout, with exit0, owned0 and cleanup0. No reader-root receipt existed. Two repaired E2E executions are documented: the first evidence wrapper expected a literal build phrase incompatible with quiet serial output; the underlying command passed both times. This is local tool evidence, not hosted SDK 10.0.114 or package inventory evidence.

One reviewed Revision 2 workflow trigger is planned. It must preserve the first failure and report actual completion independently, without relabeling old results or rerunning the unchanged first workflow.

## Publication and scope

The first expected.json create was denied for unverified destination and potentially sensitive metadata. Read-only checks established the same repository is public and every grant source file/helper matched public exact 8a13 blobs. The same exact operation was retried once on that new evidence and allowed at `5b7db7e6c2f67bb21d32a2450cd84a546c4602b8`; no alternate upload route was used.

No RPC, D2, cancellation regression, Trim/AOT or performance run is included. Assembly references and forwarders are inventory, not a compatibility verdict. No production permission change, merge or release occurs. Raw logs and traces are not uploaded.
