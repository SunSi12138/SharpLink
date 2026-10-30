# CQL3 — isolated manual-build CodeQL SARIF evidence

Local review candidate only. The dormant request grants no execution authority.
Independent exact-tree review and root release precede one new request-only push;
CQL1/CQL2 are retained unchanged and never rerun.

## Fixed source, extraction and budget

- Product c22b945f71c075c878e93a7c92bf4e45a8d33a6f, tree
  12b88cb38821e2c40bd1fb209a6244396bed52ba, is checked out clean at
  GITHUB_WORKSPACE root. Twelve reviewed wrapper files are hash-bound and staged
  outside that root in RUNNER_TEMP before the product checkout replaces them.
  No second product tree, custom source exclusion, or product edit is introduced.
- **Extraction changes from `none` to `manual`; SDK changes from 10.0.102 to
  10.0.114**, the validated final Release Gate SDK. This is not a same-context
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
c22 bytes. Static membership: **64 solution-direct + 3 ProjectReference-transitive
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
   tracked hashes must still match. Additional C# files are allowed only under
   `obj` of a statically built project; every byte hash is recorded, with owner
   identified only by project output directory. **Specific generator producer is
   UNKNOWN**, not inferred from a directory label.
2. For each of the 67 projects, require its own `project.assets.json` to name the
   exact project/output directory and evaluated target framework, and its own
   Release/TFM primary DLL and PDB. Record hashes, not bytes. Same DLL basenames
   (notably Dynamic/Replacement pairs) stay separate by project path. No PDB
   parser is included: CodeView/PDB binding and complete Compile input lists are
   explicitly NOT CHECKED/NOT OBTAINED. Outputs prove build artifacts, not each
   compilation unit's semantic extraction.
3. Read native `csharp/src.zip` after analysis and export only source paths and
   hashes, never the archive. Every C# member must map to the observed root's
   tracked/generated bytes. Permit exact bytes or explicitly reported UTF-8 BOM
   removal only; unknown transcoding fails rather than hiding a mismatch. The
   actual archive's tracked/generated counts and all missing tracked paths are
   reported separately from the old 1,383 enumeration. Each production project
   must have native tracked-source evidence. This union of source references
   still does not prove every project/TFM was semantically extracted.
4. Retain PID/start/CWD/terminal line records and hashes from native extractor
   logs, not raw arguments, arbitrary message text or broad logs. Every observed
   relevant PID needs exactly one ordered start/CWD/successful terminal. Missing
   or duplicate terminals, unknown status syntax, native ERROR-level records,
   missing logs or outside-root CWD are INCOMPLETE. No inferred PID-to-project or
   67-compilation mapping is asserted. Actual official job/compiler logs require
   independent review; status remains PENDING_OFFICIAL_JOB_LOG_REVIEW until then.
5. SARIF C# artifacts/diagnostic paths must be in the native manual source archive,
   not forced to equal the static 1,339 or buildless 1,383 set. This changed scope
   is disclosed, not used to discard error notifications.

## Failure history and safety interpretation

CQL1 run **41038355612** scanned two product copies (2,766 C# / two global.json)
from the Actions cwd and had 1,783 error diagnostics. CQL2 run **41041252157** fixed
that scope defect (root checkout, 1,383 / one) but still had **438 errors: 375
Compiler error + 63 Unexpected extractor error**, including 56 distinct production
source files. Both had zero SARIF results and are **INCOMPLETE**, not clean scans,
not proof ordinary product builds fail, and not erased by CQL3's different mode.
Official buildless limitations include dependency/project-boundary heuristics and
incomplete generated-code context. CQL3 attempts a real build, not a diagnosis
claim that one limitation explains every original error.

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
reviewed dormant tree to `validation/codeql-sarif-c22-20261030-r3`; GET tree/parent;
then create exactly one REQUEST-only child with fixed message
`run: isolated CodeQL SARIF CQL3`, non-force expected-old ref update, GET readback.
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
