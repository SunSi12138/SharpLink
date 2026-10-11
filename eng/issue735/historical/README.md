# #803 historical root-cause attribution controls

Pinned subject: `8ac4c84938d193c9a9659e9facd71f1fff82968a`. This directory is an isolated
experiment for the closed historical candidate. It does not modify current dev,
revive #803, loosen existing tests, or establish a production Go.

`apply.py` requires the exact historical Git HEAD and validates SHA-256 of all six
input files before editing. Run once against a pristine detached historical checkout:

```bash
python3 apply.py --checkout /absolute/history-red --mode red --manifest red-overlay.json
python3 apply.py --checkout /absolute/history-green --mode green --manifest green-overlay.json
```

Both modes inject the same four deterministic cases (two methods, two arguments
each). Existing tests and their oracles stay intact. The first crossing calls the
real session's shared terminal implementation after remote abort while the
stream gate prevents concurrent identity retirement. The second crossing holds
a later cancellation registration, exposing token cancellation before the writer
cleanup callback begins; the actual client producer completes while that callback
is deferred. An optional iterator exception must retain exact identity as well.

The green overlay makes only two causal corrections: reuse the existing immutable
stream identity during terminal resolution so `ValidateOpen` preserves its selected
failure, and explicitly settle the immutable client
lease in `finally` on token cancellation. Making the existing abort helper internal
allows that client call. It does not address all lifecycle, retired-key or performance
risks of #803 and must not be offered as a repaired production implementation.

Revision 1 changed `Open` globally to rethrow the selected failure. In real run
`38163848907`, its four new cases passed on four repetitions, but the unfiltered
suite failed three existing reentrant-cleanup cases because their attempt to reopen
a pinned key received `IOException` instead of the existing `SharpLinkException`
rejection contract. Those failures and the revision 1 source are retained. Revision 2
leaves `Open` entirely unchanged and limits immutable-identity reuse to terminal
delivery. No existing test assertion or timeout changes. The still-unresolved case
where retirement finishes before terminal resolution is outside this causal fixture;
the complete producer-to-terminal lease design would require broader production work.

Build each overlaid checkout independently with the unchanged UnitTests project.
Run the four new cases using the normal test platform filter:

```bash
dotnet test --project test/SharpLink.UnitTests/SharpLink.UnitTests.csproj -c Release --no-build --treenode-filter '/*/*/*/Historical735*'
```

Retain build logs, raw stdout/stderr, exit codes, overlay manifests, Git diffs,
binary hashes, .NET information, and every repeated run. Expected red means
four test failures with successful compilation, not any generic nonzero exit.
Green then requires those same four assertions to pass; it is attribution evidence
only. Run the unfiltered suite afterward to detect interference from the overlay.
