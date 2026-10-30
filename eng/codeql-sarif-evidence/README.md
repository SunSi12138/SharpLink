# CQL7 — bounded structured compilation and package evidence

Local source/offline review candidate only. Dormant REQUEST grants no execution
authority. Exact independent review and root release must precede any new CI.
CQL1/2/3/4/5/6 original failures and missing bytes remain unchanged; no rerun.

## Fixed source and security scan

Source `d39aa31948461e16264be54a69be231c8fb5882d`, tree
`2c1fe56b793ce06cff6fb63c9e633c67d4fd1ac3`, remains fixed. Its sole change from
original c22 is the expressly authorized nine-variable test syntax refactor:
explicit locals plus the same deconstruction assignment. Nine/ten-output contracts,
RHS evaluation and every assertion remain; original var-declaration inference
syntax coverage is reduced. No further product or test change is introduced.

All reviewed wrapper files are hash-bound and staged outside GITHUB_WORKSPACE,
then a clean source checkout replaces the request checkout at workspace root.
1821 tracked files,1383 C# files and exactly one global.json must match the source;
no cached bin/obj outputs or second source tree. The auxiliary helper is compiled
outside product root, before CodeQL init, and cannot become a scanner input.

Unchanged primary scan: SDK10.0.114, manual full-solution restore/Release build,
CodeQL action `9b3254085494b55a0f7c4c2b3d2d52cc712ce370`, CLI2.27.3,
query pack1.10.2, category `/language:csharp`, original exact59 query paths including
diagnostics/metrics/telemetry (not59 security rules). Default config remains empty,
no filters/overlay/cache/debug or security threshold changes. Official bundle
SHA256 `64518dc413c8e7b5733fee2489e830a899bb1e3f28c8873b5869d5cc90164c5c`.
Original command arguments, warnings/audit policy and process_host byte-level
lifecycle implementation remain unchanged. No tests or product executables run.

Existing permissions remain actions/contents/packages read and security-events
write. `upload: never`, `upload-database: false`; no new GitHub analysis. Historical
GitHub alert disposition remains unknown, regardless of this scan's results.

## Bounded supplement and compatibility preflight

CQL5's original resolve command succeeded, but the wrapper rejected the actual
`qlPackName` JSON field before the forensic query ran. Its exact original merged
stdout/stderr receipt is a frozen offline fixture, not new execution evidence.
The resolver requires the five observed fields, exact pack/relative names, empty
compilationCache array, canonical fixed schema and bundle-only library paths.
Unknown fields, types, paths and library identities still fail closed.

1. Before init, build the unchanged BCL-only NuGet reader in runner TEMP using
   SDK114 and no external PackageReference. It is not product/scanner input.
2. After init and actual toolchain identity, but BEFORE product restore/build,
   resolve the independent forensic library/schema and run the official
   `codeql query compile --check-only`. This validates QL syntax/types only; it
   neither evaluates database rows nor establishes source coverage. A nonzero,
   timeout, cancellation or unsafe process stops product restore/build. Its
   original combined command output and partial receipts survive failure.
3. Run the unchanged manual solution build and original59-query analysis.
4. After successful analysis and safe product lifecycle, preserve the one actual
   resolved TUnit.Core1.73.19 nupkg, precise assets fields, materialized props/member
   bytes and official SDK GetContentHash result BEFORE the independent QL query.
   Raw ZIP SHA512 and NuGet content hash remain distinct; no custom ZIP algorithm
   or sidecar substitution. This is not certificate/signature trust verification.
   Existing restore signature policy and exact source/member checks are unchanged.
   Only a safely completed observation whose sole problem is a content-hash
   comparison mismatch may let the independent QL proceed. The mismatch, actual
   values and bytes remain recorded; package status and final campaign stay
   INCOMPLETE. Unknown/structural/API errors, timeout/leak/cancel do not proceed.
   Next QL requires package step outcome success AND safe output AND !cancelled();
   a previously written receipt alone cannot authorize it.
5. Revalidate the exact preflight receipt, query/manifest and resolved library
   bytes; evaluate the single independent forensic QL against the finalized DB,
   then decode opaque Compilation IDs and all structured relations. It remains
   separate from the original59 results, never a60th security-rule selection.
   No registry/network pack install or default-cache fallback is introduced.
6. Retain every Compilation even with missing relationships: full cwd/output,
   inputs/references, diagnostics and finished timing. IDs are not PIDs; finished
   does not mean error-free. All compiler severity3, global extractor severity5
   and original SARIF errors remain blocking; no raw args/environment exported.
   Require actual full obj/bin path plus byte-hash mapping for every67 project
   output, not exactly67 Compilation entities or basename matching. Extra contexts
   must be explicitly verified; unknown/missing/ambiguous inputs remain INCOMPLETE.
   References retain path/hash, not unproven package origin. File membership is
   not proof of perfect semantic extraction.

Budgets remain overall60min, SDK30s, restore600s, product build1200s, primary
analyze15min, helper build120s and package helper60s. Preflight resolve/schema/check
and post-build query/decode SHARE300s of active elapsed time (intervening product
build/analysis is excluded). Existing owned-process cleanup is unchanged. Any
unsafe or unknown lifecycle prevents the next workload; final collection is pure
file processing. No retry, report replacement, or historical reclassification.

## Signed opaque IDs and separately retained file observations

CQL6's official independent QL evaluation and decode both succeeded. Its original
rows contain negative diagnostic Entity IDs; the prior Python nonnegative rule was
unsupported. CQL7 preserves signed integer identities exactly and still rejects
booleans, floats and strings. No abs/coercion or severity relaxation is allowed.
All11 result schemas, identities, relationships, errors and final semantics remain
mandatory. The complete original rows are used only for local independent
regression, not added to this repository or new persistent evidence archive.
CI controls use small synthetic11-table fixtures with signed IDs and invalid
relationships/severities. Original CQL6 remains INCOMPLETE.

Before product restore/build, a small read-only preflight reuses the same SDK
reference metadata parser on the actually installed SDK114 and its required
Microsoft.NETCore.App.Ref/net10.0 pack. It assumes no restored NuGet packages or
other platform packs, runs no CLR/query, retains selected safe metadata fields and
hashes in `sdk-reference-preflight.json`, and fails on missing/unknown format.
This is metadata compatibility evidence, not a new security threshold.

After a safely completed decode, its original JSON hash is retained even if later
schema interpretation fails. Final collection separates safe file observations
from aggregate semantic validation. Only the strict5 path-relationship tables are
needed to consider a file; every path must prove its role BEFORE any file read.
Exact67project/configuration/TFM/assembly paths bound compiler obj/bin outputs and
source reference/refint paths. Ref/refint assemblies are NOT assumed equal to bin;
only the actual compiler output obj versus bin relation requires byte equality.
NuGet references require the exact context's current assets identity/hash, package
folder, selected package and library path/member inventory; no broad cache root
exemption. SDK references require actual pinned SDK metadata and exact bound
ref-pack member identity, not any absolute SDK path. Existing tracked/generated
and external package input provenance must remain byte-bound.

The allowlisted `compilation-file-hashes.json` keeps each safely observed path,
role/proof, SHA256 and byte count before aggregate semantic checks. Unknown paths
retain metadata/problems only and are not read. One failure cannot erase other
legally observed hashes, but every unknown/error still makes final evidence
INCOMPLETE. Final semantic validation consumes and rebinds these observations;
partial hashes are neither semantic completion nor permission to ignore errors.
No arbitrary external path read, raw assembly export, product-source modification,
new workload or new security query is added.

## Explicit coverage and logger boundaries

Static ledger remains97 projects:64 direct +3 transitive built,30 unreached; all10
production projects included. There are1339 static candidate C# files and44 outside,
including21 original CQL2 diagnostics. Those remain visible, not waived fixes.
Actual64Release+3 named Debug outputs must be verified, without fallback config.
Same-basename Dynamic/Replacement project identities remain separate.

Exact Generator.Tests None-copy is byte-bound content-copy, not Compile coverage.
Other unknown bin C# fails. Generated obj files carry actual bytes/owner-directory
identity, not a guessed generator producer. Native source archive bytes and
workspace bytes must match (only explicit UTF-8 BOM removal supported). All
partial/unknown entries are retained before failure. Exact post-hash tracked SARIF
enumeration may be absent from archive but contributes no extraction proof;
generated/external sources require native provenance. Success-marker/archive
inconsistency is INCOMPLETE; success markers cannot override errors.

Narrow extractor INFO logging remains enabled, without global action debug.
The official shared logger is best-effort, not a reliable per-PID completion
protocol. CQL7 records `nativePidClosure=INCOMPLETE` for missing contexts as an
independent observation, never rewriting missing terminals to success. Actual
native ERROR, explicit FAILED terminal (including INFO level), unsupported relevant
records and out-of-root CWD still fail. This is a
new protocol requiring structured Compilation evidence separately; it does not
retroactively repair CQL4's51PID evidence or prove67 old contexts complete.

No PDB parser or CodeView binding is introduced. Output/metadata hashes, source
archive and structured relationships establish only their explicitly observed
scope, not all repository code or every possible compilation configuration.

## Raw evidence and acceptance

Full original SARIF is retained before checks. All findings include rules, levels,
security severity, suppressions and baseline state; no filter/dismissal. Any raw
SARIF error or failed structured/package/provenance check remains INCOMPLETE.
`executionEvidence=COMPLETE`, if achieved, is bounded execution evidence, not
security acceptance: `securityDecision=NOT_ASSESSED_REQUIRES_INDEPENDENT_REVIEW`.
Actual official logs/source/tool/query mappings and all findings require independent
original-artifact review. No performance/mobile failure acceptance is implied.

Only explicit3-day paths are exported: SARIF, identity/hash/scope/process/structured
JSON receipts; the single actual TUnit nupkg and its two precise materialized
props/source files. No product-source archive, database, BQRS bytes, SDK/runtime,
unrelated packages/cache, raw arguments/environment/config, token or credentials.
BQRS gets a hash/size plus original decoded JSON, not binary export. Metadata is
field-allowlisted, not raw registry/config/source URL data. Any unlisted path or
symlink is refused; partial known evidence still retains its INCOMPLETE state.

After independent exact-head review and separate root release: preflight new
`validation/codeql-sarif-c22-20261030-r7` branch/runs, publish reviewed dormant tree,
GET exact source parent/tree, then one REQUEST-only child with fixed message
`run: isolated CodeQL SARIF CQL7`, non-force expected-old update and GET readback.
No PR967/main/dev mutation, merge/tag/NuGet/release/permission change.

## Official source basis

- Frozen action: https://github.com/github/codeql-action/tree/9b3254085494b55a0f7c4c2b3d2d52cc712ce370
- Frozen CodeQL: https://github.com/github/codeql/tree/a1575b98a8cc19bcfa7bb6cb41be688cb3dae65c
- Compilation API: csharp/ql/lib/semmle/code/csharp/commons/Compilation.qll
- Diagnostic API: csharp/ql/lib/semmle/code/csharp/commons/Diagnostics.qll
- SDK114 NuGet source: https://github.com/dotnet/dotnet/tree/35451b769022ebedaceff05fa95fb15d5b83d3b9/src/nuget-client
- NuGet content-hash semantics: https://github.com/NuGet/Home/wiki/Nupkg-Metadata-File

- Official QL preflight: https://docs.github.com/en/code-security/reference/code-scanning/codeql/codeql-cli-manual/query-compile (`--check-only`; not query evaluation).
