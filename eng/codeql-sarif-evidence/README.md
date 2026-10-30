# CQL4 — isolated manual-build CodeQL SARIF evidence

Local review candidate only. The dormant request grants no execution authority.
Independent exact-tree review and root release precede one new request-only push;
CQL1/CQL2/CQL3 are retained unchanged and never rerun.

## Fixed source, extraction and budget

- Product d39aa31948461e16264be54a69be231c8fb5882d, tree
  2c1fe56b793ce06cff6fb63c9e633c67d4fd1ac3, is checked out clean at
  GITHUB_WORKSPACE root. Twelve reviewed wrapper files are hash-bound and staged
  outside that root in RUNNER_TEMP before the product checkout replaces them.
  No second product tree or custom source exclusion is introduced. The only source
  change from original c22 is the explicitly authorized nine-local test syntax
  refactor; production sources and all assertions remain unchanged. The original
  `var (...)` declaration/type-inference syntax coverage is no longer exercised.
  Nine-output and ten-output Deconstruct contract checks remain. This is not a
  claim the new extractor run succeeds or that it analyzes original c22 bytes.
- **Extraction stays `manual` and SDK stays 10.0.114 as in CQL3**. Compared
  with CQL2 buildless mode this still changes extraction and SDK. This is not a same-context
  rerun, proof the SDK caused the old errors, or security-rule relaxation.
- Initialize CodeQL, then run the original Gate's complete solution restore and
  Release build, with only explicit tracing/rebuild switches:
  `dotnet restore Sharplink.slnx --disable-build-servers -nodeReuse:false`
  and `dotnet build Sharplink.slnx --no-restore -c Release -v minimal
  --disable-build-servers --no-incremental -nodeReuse:false
  -p:UseSharedCompilation=false -p:EmitCompilerGeneratedFiles=true`.
  No test/product executable, extra probe, MSBuild observation target, or new
  assembly is run. Existing warning, audit, and error policies remain intact.
  Build-server reuse is disabled. Generated files are retained by the compiler.
- Fixed maximum job budget **60 minutes**, no retries: bundle transfer at most
  600 seconds, SDK identity at most 30 seconds, restore at most 600 seconds,
  build at most 1200 seconds. The reviewed FLUSH1-style owned process host uses
  a separate session, bounded wait, SIGKILL on timeout/cancel/leaked descendants,
  bounded reaping and `ps` non-zombie group-quiescence checks. Original exit and
  timeout/cancel/leak/cleanup flags remain recorded. An uncertain, timed-out,
  cancelled or leaked process blocks all subsequent compiled/analyze workloads;
  only existing-file evidence collection remains. A normal nonzero exit with
  confirmed quiescence may continue diagnostic analysis, never completeness.
  Analyze has a 15-minute step
  budget; the overall 60-minute cap also includes setup/post/evidence work.
  Restore/build failure is retained. Analyze still attempts to retain available
  extraction diagnostics, but partial build outputs can never complete evidence.
- Unchanged action 9b3254085494b55a0f7c4c2b3d2d52cc712ce370, CLI **2.27.3**,
  csharp query pack **1.10.2**, category `/language:csharp`, and original exact
  **59 query paths**, including diagnostics/metrics/telemetry, not 59 security
  rules. Default configuration must remain empty; no filters/model overrides.
- Official Linux64 `.tar.gz` bundle SHA256:
  64518dc413c8e7b5733fee2489e830a899bb1e3f28c8873b5869d5cc90164c5c.
  Actual version, query-file bytes, and all 59 BQRS hashes are checked. None of the
  installed runtime/SDK/CodeQL/database/BQRS bytes are archived.
- Existing actions/contents/packages read and security-events write permissions
  are unchanged. `upload: never`, `upload-database: false`; no new GitHub analysis
  is submitted. No cache, debug/combined-SARIF archive, overlay or diff-range scan.
  Safe effective-config receipts are exported, never the raw config/environment.

## Scope ledger: not whole-repository equivalence

`coverage-ledger.json` binds all **97 csproj** and **1,383 tracked C# files** to
the authorized test-syntax source bytes. Static membership: **64 solution-direct + 3 ProjectReference-transitive
= 67**, including all **10 production projects**; **30 unreached** projects remain
outside this solution build. The 33 non-direct projects are individually listed
with purpose and reference conditions: they are not all excluded (three build).
Static source candidates number 1,339; 44 tracked files are outside that static
candidate set. Those 44 carry **21 original CQL2 error notifications**. Their
absence from a manual scan cannot be counted as fixes or waived diagnostics.
Static candidate membership is never reported as actual extraction coverage.

Evidence is deliberately limited to ordinary artifacts and CodeQL's own output:

1. Before execution, require the exact 1,821 tracked file hashes, 1,383 C# paths,
   one global.json and no cached project bin/obj outputs. After execution, all
   tracked hashes must still match. Additional generated C# files are allowed only under
   `obj` of a statically built project. The exact Generator.Tests csproj `None`
   copy of SharpLinkMetadata.cs to SharpLinkMetadata.source.cs is separately
   byte-bound as content-copy, not Compile evidence. Unknown bin C# remains a
   failure; partial inventories survive errors. Every byte hash is recorded, with owner
   identified only by project output directory. **Specific generator producer is
   UNKNOWN**, not inferred from a directory label.
2. For each of the 67 projects, require its own `project.assets.json` to name the
   exact project/output directory and evaluated target framework, and its own
   configuration/TFM primary DLL and PDB: 64 direct Release plus the three named
   transitive Debug outputs observed in original CQL3. Actual assets and original
   build output must be independently cross-checked. Record hashes, not bytes. Same DLL basenames
   (notably Dynamic/Replacement pairs) stay separate by project path. No PDB
   parser is included: CodeView/PDB binding and complete Compile input lists are
   explicitly NOT CHECKED/NOT OBTAINED. Outputs prove build artifacts, not each
   compilation unit's semantic extraction.
3. Read native `csharp/src.zip` after analysis and export only source paths and
   hashes, never the archive. Every C# member must map to the observed root's
   tracked/generated bytes, or the precisely resolved TUnit.Core 1.73.19
   buildTransitive/net10.0/TUnit.Core.GeneratedNamespace.cs member. That single
   external source requires actual assets resolution, nupkg byte SHA512 matching
   assets contentHash, and member/materialized/archive content correspondence;
   arbitrary NuGet paths are not allowed. Permit exact bytes or explicitly reported UTF-8 BOM
   removal only; unknown transcoding fails rather than hiding a mismatch. The
   actual archive's tracked/generated counts and all missing tracked paths are
   reported separately from the old 1,383 enumeration. Each production project
   must have native tracked-source evidence. This union of source references
   still does not prove every project/TFM was semantically extracted.
4. Retain PID/start/CWD/terminal line records and hashes from native extractor
   logs, not raw arguments, arbitrary message text or broad logs. The official
   narrow CODEQL_EXTRACTOR_CSHARP_OPTION_LOGGING_VERBOSITY=info supplies INFO
   lifecycle records without action debug or global verbosity changes; additional
   logging overhead is disclosed and no existing error/terminal guard is relaxed. Every observed
   relevant PID needs exactly one ordered start/CWD/successful terminal. Missing
   or duplicate terminals, unknown status syntax, native ERROR-level records,
   missing logs or outside-root CWD are INCOMPLETE. No inferred PID-to-project or
   67-compilation mapping is asserted. Actual official job/compiler logs require
   independent review; status remains PENDING_OFFICIAL_JOB_LOG_REVIEW until then.
5. SARIF C# references are separated into native-archived and exact post-hash
   tracked enumeration. A tracked artifact absent from the archive is explicitly
   enumerated-not-archived, never counted as extraction or native completion.
   Generated/external sources still require native provenance. Diagnostic error
   records remain blocking even for enumerated-only tracked files. File-level
   success markers absent from the native archive are exposed as INCOMPLETE; a
   marker never overrides a semantic error. No hardcoded 44-file assumption or
   1,339/1,383 equality is imposed on actual extraction. URI/base/index/duplicate
   and unknown path guards remain strict.

## Failure history and safety interpretation

CQL1 run **41038355612** scanned two product copies (2,766 C# / two global.json)
from the Actions cwd and had 1,783 error diagnostics. CQL2 run **41041252157** fixed
that scope defect (root checkout, 1,383 / one) but still had **438 errors: 375
Compiler error + 63 Unexpected extractor error**, including 56 distinct production
source files. Both had zero SARIF results and are **INCOMPLETE**, not clean scans,
not proof ordinary product builds fail, and not erased by CQL4's different mode.
Official buildless limitations include dependency/project-boundary heuristics and
incomplete generated-code context. CQL4 attempts a real build, not a diagnosis
claim that one limitation explains every original error.

CQL3 run **41046223356** built 67 projects (64 Release + three Debug), with
0 compiler warnings/errors, but retained one real IndexOutOfRange extractor error
in RpcMethodDescriptorTests nine-variable `var` deconstruction. It remains
**INCOMPLETE**. Its four provenance assumption failures (None-copy, Debug outputs,
external TUnit source, warnings-only native log) are not evidence of product
build failure and cannot be repaired retrospectively from missing bytes. CQL4
uses an explicitly authorized test syntax tradeoff and narrow evidence repairs,
not filtering/suppressing the old error or claiming a verified scanner fix.

Raw SARIF is copied before completeness checks. Findings retain every result,
rule, level, security-severity, suppression and baselineState. Error-level tool
execution/configuration notifications, failed actions/build, changed identities,
missing query results or provenance mismatches remain INCOMPLETE. Independent
post-checks continue where possible, preserving partial receipts without making a
complete summary. There is no retry, error filter or automatic dismissal.

`executionEvidence=COMPLETE` describes the bounded execution evidence only.
Finding level=error or high/critical severity does not become a safety PASS;
`securityDecision=NOT_ASSESSED_REQUIRES_INDEPENDENT_REVIEW`. Historical GitHub
open/dismissed alerts remain unknown. This is not full-repository equivalence,
proof of flawless semantic extraction, acceptance of pending performance risks,
or erasure of the standalone mobile failure.

## Artifact and activation

Only the explicit SARIF/findings/identity/version/hash/scope/coverage manifests and
incomplete receipts are uploaded, **3-day retention**. No source, DLL/PDB, SDK,
cache, token, raw config, raw compiler arguments, database, or archive bytes.
Hash-only evidence is not an independently reproducible byte archive.

After exact independent review and root release: precheck branch/runs; publish
reviewed dormant tree to `validation/codeql-sarif-c22-20261030-r4`; GET tree/parent;
then create exactly one REQUEST-only child with fixed message
`run: isolated CodeQL SARIF CQL4`, non-force expected-old ref update, GET readback.
No PR967/main/dev mutation, merge/tag/release/NuGet/permissions change. Monitor only
that first attempt, preserve failure, verify official original ZIP digest, and
submit raw SARIF and original official job logs for independent security review.

Offline controls (Python/git only; no local CLR/CodeQL):
`PYTHONDONTWRITEBYTECODE=1 python -m unittest discover -s
eng/codeql-sarif-evidence -p 'test_*.py' -v`.

## Official basis

- [Frozen action schemas](https://github.com/github/codeql-action/tree/9b3254085494b55a0f7c4c2b3d2d52cc712ce370)
- [Build options and C# accuracy boundaries](https://docs.github.com/en/code-security/reference/code-scanning/codeql/build-options-for-compiled-languages)
- [Manual extraction covers built code](https://docs.github.com/en/code-security/how-tos/find-and-fix-code-vulnerabilities/manage-your-configuration/codeql-for-compiled-languages)
- [Native source archive semantics](https://codeql.github.com/docs/codeql-overview/codeql-glossary/)
- [Checking missing source via src.zip](https://docs.github.com/en/code-security/reference/code-scanning/troubleshoot-analysis-errors/fewer-lines-scanned-than-expected)
- [Official extractor log writer](https://github.com/github/codeql/blob/a1575b98a8cc19bcfa7bb6cb41be688cb3dae65c/csharp/extractor/Semmle.Extraction.CSharp/Extractor/Analyser.cs)
- [dotnet build options](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-build)
