# Amendment04: one fixed byte-only ARM revalidation, same compiled bytes

**Review before execution.** Independent review of prepare40716132624 passed the
Linux bundle/receipts and confirmed the ARM final-official-member provenance;
it did not pass the original failed ARM receipt. This amendment fixes that
validation model and recovers existing ARM build bytes, not a new build/prepare.
Both old prepare runs and their original conclusions/artifacts remain retained.

## Fixed final-member rule

Runtime-only archive order is exactly Core10.0.14 then ASP10.0.14, as already
executed by the original installer. Build SDK order was SDK102→Core14→ASP14.
`runtimeArchiveOrder` is now a required exact plan field. From the SHA512-verified
archives, derive a deterministic complete final file/directory set. For every
file retain path, final component, archive SHA512 and member SHA256. Across
archives last writer wins; within an archive duplicates/aliases, noncanonical
paths, links/special members and file/directory collisions are errors. Compare
all installed files/directories and bytes to the resulting one expected map.
No installed-byte-dependent source choice,13-file exception, signing, byte
normalization or hash ignore exists. Earlier-Core conflict bytes are rejected.
The collector saves the manifest before launching each fixed probe; workload and
all actual probe/decoder code remain unchanged. Identity checking is not timing.

## Fixed recovery input and execution

One new request stage `revalidate`, branch `codex/wakeup-native-revalidate-v1`,
standard macos-14 only, original immutable action pins/read-only permissions,
20minute job bound and no automatic/manual rerun. Existing two-architecture
prepare/collect job is skipped entirely on this branch. Linux is not rebuilt,
revalidated, repackaged or replaced by this job.

The only input is original ARM artifact12828441906 from build run40716132624,
commit2b59e359bff99ec8f7d5b1de158179d6eb9fa4f0. Its already-reviewed ZIP digest is
b3648cb658d92509f2e74ff47dd34a4db3b4d81fdaac42504d069343eef108c5. The downloaded
artifact's original `failed-stage-files.tar.gz` must hash exactly
8fbd4fb3cf24c7859aa184fbd5c65718aa5bbf58c0f4f73f4d96db7f2dfffd70. This digest,
run, commit and artifact are constants and strict REQUEST values, not arbitrary
migration inputs. All archived bytes, including the three official archives,
are restored once at the original canonical macOS path; no regular file may be
overwritten, no escaping/link/special member accepted. Unknown environment
settings remain fail-closed; known policy is unchanged and original values saved.

`revalidate.py` performs Python byte/hash/JSON/tar operations only. It has no
network, subprocess, dotnet, compiler, signing or binary-execution operation.
It checks original prepare host/run, immutable original tool hashes, successful
original build/source-clean exit records, official archive SHA512s, final runtime
map, recorded SDK member identities, and normal full receipt closures. It cannot
claim to re-inspect the original installed build SDK: that directory was excluded
from failure artifacts. It verifies its retained member hash record against the
SDK archive instead; this limit is explicit, as in the accepted Linux evidence.
New revalidation machine uname/image/Python information is recorded separately,
without treating it as the original build host or a future collection host.

## Never relabel or overwrite the failed receipt

Every restored original file remains byte-identical. Original `receipts/D.json`
remains present and FAILED; the failure happened before an original C receipt was
written. New files are only under `revalidation/`,
`receipts/revalidated-D.json`, `receipts/revalidated-C.json` and the previously
absent prepared-index. Both new receipts preserve original D/C source stamps,
SDK/runtime pins, build logs, generated source, product/probe/decoder bytes and
archive identities. Their hash closure additionally includes new provenance and
validator sources. New provenance distinguishes original build run/commit and
failed artifact/tar/receipt hashes from current revalidation run/tool commit and
new plan/tool hashes. `buildExecuted`, `clrExecuted`, `binaryBytesChanged` are false.
The new bundle includes original tools alongside distinctly located new tools.

Revalidation produces `wakeup-revalidate-osx-arm64-<new-run>` with prepared.tar.gz,
new receipt tuple, original logs and new provenance/final-member manifest. Failure
or timeout retains all available original/partial output via always-upload; whole
runner loss remains missing evidence, not fabricated success. No retry or build
fallback exists. Actual output still needs independent receipt/bundle review.

## Existing two-architecture collection approval gate retained

Collection REQUEST still requires both architecture bundle/D/C receipt SHA256s.
Its `prepareRunId` and `prepareCommit` remain the real original build40716132624 /
2b59e359, and new `revalidation:{runId,toolCommit}` separately identifies the ARM
revalidation artifact/tool identity. A Linux-only request, fake ARM digest or
reusing the failed run as the revalidation run is rejected. The example remains
invalid until actual reviewed recovery digests/run/tool identity are filled.

Linux downloads exactly its original successful artifact/bundle and uses original
D.json/C.json unchanged. ARM downloads the new separately approved revalidation
artifact, verifies original build identity AND new revalidation identity, and uses
explicit revalidated-D/C.json. Both preserve original decoder/probe/product bytes.
Both invoke the newly reviewed checkout's collector/validator against those old
binaries, using the new explicit-order plan/tool lock; restored original native
tools are retained as evidence, not silently overwritten/executed. Original tool
hashes are pinned, and ARM bundle's new wrapper/workflow must match collection's
wrapper/workflow bytes. Native file lock binds the updated collector/validator.
Actual collect host identity remains separately checked on each architecture.

No collection runs until both receipts and the final REQUEST are independently
approved and root authorizes. Original16-cell maximum, fixed8-cell per-arch order,
natural Tier1 behavior, failure-stop/no-retry/no-replacement rules and all workload
parameters stay unchanged. A successful recovery/capture is not a performance win.
