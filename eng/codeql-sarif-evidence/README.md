# CQL5 — bounded structured compilation and package evidence

Local source/offline review candidate only. Dormant REQUEST grants no execution
authority. Exact independent review and root release must precede any new CI.
CQL1/2/3/4 original failures and missing bytes remain unchanged; no rerun.

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

## New bounded supplement, not an extra security-rule selection

1. Before init, build a BCL-only NuGet reader in runner TEMP, with no external
   PackageReference and with exact SDK114. This is an evidence tool, not a product
   workload or scan input. Its own bytes, loaded official SDK DLL identities and
   fixed method binding must be recorded. Build uses owned-process cleanup.
2. Run the unchanged original manual build and59-query analysis.
3. Only after successful analysis, run one separately located forensic QL query
   against that database. Its output is outside original59 result paths and never
   counted as a60th security query. Frozen csharp-all7.4.1 and resolved bundle-only
   library/schema bytes are recorded; no pack-install/network registry fallback.
4. Decode opaque database Compilation IDs and complete structured relationships:
   cwd, full output assembly path, input files, references, diagnostics and finished
   timing. Every Compilation is represented even if relationships are missing.
   A database ID is not a PID; a finished record is not proof of zero errors.
   Compiler error severity3 and global extractor error severity5 remain blocking,
   alongside all original SARIF errors. Arguments/expanded arguments are not read
   into exported receipts. Missing/ambiguous/unknown mappings remain INCOMPLETE.
5. Map full actual csc output paths (often obj) to each exact project's existing bin
   DLL by actual byte hashes and directory ownership, never by basename. Require
   all67 project outputs to have verified mappings, but do not require exactly67
   Compilation entities. Additional contexts must be individually listed and
   verified; they are not silently discarded or merged. References retain actual
   canonical paths/hashes without claiming package origin. Inputs must agree with
   verified native source/archive bytes. No per-file semantic perfection is claimed.
6. After all CodeQL workloads, retain the one actual resolved TUnit.Core1.73.19
   nupkg and bounded asset-field provenance, before comparing anything. The BCL
   helper calls the SDK's official NuGet.Packaging content-hash implementation,
   not a hand-written ZIP normalization or sidecar trust. Raw ZIP SHA512 and NuGet
   signed-package content hash have different semantic labels. Record both values,
   algorithms, size, exact assets fields and loaded method/assembly identities.
   GetContentHash does not verify certificate/signature trust. Existing restore
   signature policy is not bypassed. Props/member/materialized/native-archive
   correspondence is independently checked; no blanket NuGet cache exemption.

Shared process budgets remain bounded: overall60min, SDK30s, restore600s,
product build1200s, primary analyze15min. New helper build120s, forensic commands
share300s and package helper60s. Each new command uses the existing owned-process
host; timeout/cancel/unknown group status stops subsequent workloads. Workflow
requires successful prerequisite step outcomes and !cancelled(), not merely a
previously written safe flag. Failed checks retain original partial receipts and
never trigger retries. Final collection is pure file processing.

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
protocol. CQL5 records `nativePidClosure=INCOMPLETE` for missing contexts as an
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
`validation/codeql-sarif-c22-20261030-r5` branch/runs, publish reviewed dormant tree,
GET exact source parent/tree, then one REQUEST-only child with fixed message
`run: isolated CodeQL SARIF CQL5`, non-force expected-old update and GET readback.
No PR967/main/dev mutation, merge/tag/NuGet/release/permission change.

## Official source basis

- Frozen action: https://github.com/github/codeql-action/tree/9b3254085494b55a0f7c4c2b3d2d52cc712ce370
- Frozen CodeQL: https://github.com/github/codeql/tree/a1575b98a8cc19bcfa7bb6cb41be688cb3dae65c
- Compilation API: csharp/ql/lib/semmle/code/csharp/commons/Compilation.qll
- Diagnostic API: csharp/ql/lib/semmle/code/csharp/commons/Diagnostics.qll
- SDK114 NuGet source: https://github.com/dotnet/dotnet/tree/35451b769022ebedaceff05fa95fb15d5b83d3b9/src/nuget-client
- NuGet content-hash semantics: https://github.com/NuGet/Home/wiki/Nupkg-Metadata-File
