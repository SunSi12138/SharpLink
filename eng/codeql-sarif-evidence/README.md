# CQL2 — isolated full-source CodeQL SARIF evidence

**A local review candidate; no authorization is implied by this document.** Release
requires independent review of the exact tool tree, root release, and one request
child. The dormant `REQUEST.json` does not authorize execution.

## Scope and budget

- Analyze product commit `c22b945f71c075c878e93a7c92bf4e45a8d33a6f`, exact tree
  `12b88cb38821e2c40bd1fb209a6244396bed52ba`, as the clean **GITHUB_WORKSPACE root**.
  First verify the request checkout, copy only the eight hash-bound verification
  files (no `.git` or product files) to `RUNNER_TEMP/cql2-tools`, then clean checkout
  c22 over the workspace. Run wrappers from the external copy; separately record
  reviewed tool, request and source identities. Before/after tracked source hashes
  and the physical workspace inventory must agree: exactly the original 1,383 C#
  files and one root `global.json`, no additional/generated C# file or symlink tree.
  `source-root: .` and `checkout_path: GITHUB_WORKSPACE` match the original scanner
  working directory. No custom query filter is used to hide a duplicate checkout.
- One Ubuntu hosted job, maximum 35 minutes (including a bounded 10-minute bundle
  download), first attempt only. No rerun, optimization, product edit, PR update,
  merge, tag, release, NuGet publication, or permission change.
- Preserve the original run `41026509659` configuration: same CodeQL action pin
  `9b3254085494b55a0f7c4c2b3d2d52cc712ce370`, C#, `build-mode: none`, unchanged
  default queries, category `/language:csharp`. No custom query/config/pack filters.
- Freeze original actual CLI **2.27.3** with the official 663 MB Linux64 `.tar.gz`
  bundle SHA256 `64518dc413c8e7b5733fee2489e830a899bb1e3f28c8873b5869d5cc90164c5c`.
  The `.tar.zst` is a different artifact/hash and is not used. Verify actual CLI
  version and `codeql/csharp-queries` **1.10.2** on the hosted runner.
- Verify exact **59 query result paths** against the original job `123965792932`
  log. They include diagnostic, telemetry, and metric queries; these are **not 59
  security rules**. Bundle/pack/query hashes and BQRS result hashes are recorded;
  database and installed toolchain bytes are not archived. This is identity
  evidence, not a self-contained reproduction package.
- Existing permissions are unchanged: actions/contents/packages read,
  security-events write. The supported `upload: never` and
  `upload-database: false` inputs prevent this scan from submitting a new GitHub
  code-scanning analysis/database. TRAP/dependency caches are disabled. Ordinary
  action status telemetry may still be sent by the pinned action. Before init,
  reject debug, combined-SARIF debug, overlay/default-branch and alternate-temp
  overrides that could implicitly archive extra bytes. Verify effective scope
  after init and analysis: none/no cache/no diff ranges/no query/path filtering.
  Only a safe scalar/empty-field receipt and config hash are exported, not the
  underlying action configuration.

## Why CQL1 remains INCOMPLETE

CQL1 run `41038355612` is retained unchanged, not rerun or declared green. It put
full product copies in `tools/` and `source/`. The pinned action's
`extractUsingBuildMode` explicitly runs `trace-command --working-dir process.cwd()`:
`source-root` initialized the database but did not change that working directory.
The actual buildless scan found **2,766 C# files / two global.json files**, versus
**1,383 / one** in original run `41026509659`. Its raw SARIF had zero results but
1,783 error diagnostics, so completeness correctly failed. Those diagnostics are
not evidence that the ordinary product build fails, nor proof that source-only
extraction would reproduce them. The root-level CQL2 layout fixes this tooling
scope defect without changing scanner version, build mode, queries or diagnostic
acceptance rules. Original and CQL1 logs both used SDK10.0.102/host10.0.2; no SDK
upgrade/downgrade is being introduced here.

## Evidence and interpretation

The temporary artifact (3-day retention) explicitly allowlists raw `csharp.sarif`,
request/source/toolchain identity, query-result hashes, action outcomes, findings,
completion/incomplete status, staged-tool hashes, physical/SARIF scope receipts
and artifact hashes. It never broadly uploads the
runner temp directory, source checkout, cache, CodeQL database, environment,
configuration, or credentials. Original SARIF bytes are copied before campaign
completeness checks; failures retain whatever evidence exists and fail the job.
Even when SARIF contains error diagnostics, the collector attempts the independent
post-scan source/scope/query checks and retains their outcomes before failing. This
improves failure evidence, not the acceptance threshold.

The SARIF C# artifact URI set must exactly equal the tracked 1,383 C# files. Absolute
C# paths in execution/configuration diagnostics must resolve to that same set;
external staged-tool or temporary-source references fail scope checks. No unknown
generated C# provenance is silently accepted. Physical and URI inventories do not
claim semantic extraction success. The independent terminal review must also read
the **original official job log** and confirm the actual `trace-command` working
directory, `Found 1383 source files`, unique global.json, and actual SDK/host. Until
that log review, actual trace scope is `PENDING_OFFICIAL_JOB_LOG_REVIEW`, regardless
of CI color. No debug mode, extra CLI logging flags, or broad log archive is added.

`findings.json` retains every SARIF result and full referenced rule metadata,
including `security-severity`, effective/reported `level`, `suppressions`, and
`baselineState` when present. No result is filtered or automatically dismissed.
Security severity display bands (critical >= 9, high >= 7, medium >= 4, low below
4) do not implement an acceptance threshold. Missing severity stays missing.

`completion.json: executionEvidence=COMPLETE` means only that the exact source,
toolchain, query result set, action outcomes and SARIF execution checks completed.
A finding with `level=error` or high/critical security severity **does not make the
execution incomplete, and does not become a security PASS**. Security decision
remains `NOT_ASSESSED_REQUIRES_INDEPENDENT_REVIEW`. Tool invocation failure or
error-level execution/configuration notifications, missing/extra SARIF, changed
source/toolchain/query set, or missing evidence yields INCOMPLETE/failure, never
an invented zero-finding result. Warnings/diagnostics remain in raw and summary.

This fresh full-source scan can describe current findings under this configuration.
It cannot reconstruct GitHub historical open/dismissed alert disposition, cannot
prove all C# constructs were extracted perfectly, and does not erase original
standalone mobile failures or accept pending performance risks. Original PR check
“no new alerts” and this scan have different scopes.

## Activation (only after root release)

1. Precheck the dedicated branch and prior runs to prevent duplication.
2. Publish exact reviewed dormant tool tree on
   `validation/codeql-sarif-c22-20261030-r2`; read back tree and parent.
3. Create one child changing only `eng/codeql-sarif-evidence/REQUEST.json`, with
   fixed message `run: isolated CodeQL SARIF CQL2`. Fill exact remote reviewed
   parent/tree, product source/tree, review PASS and root release. Non-force
   expected-old ref update only. Read back the request-only delta.
4. Monitor that single first attempt, retain failures without retries, download
   original artifact, verify official ZIP digest and all hashes, and send originals
   for independent findings/security review. CI green is not security acceptance.

Offline controls: `PYTHONDONTWRITEBYTECODE=1 python -m unittest discover -s
eng/codeql-sarif-evidence -p 'test_*.py' -v`. Controls use synthetic fixtures and
mock the CLI boundary; no local CLR, CodeQL scan, or network execution is required.

## Official references

- [Frozen init input/output schema](https://github.com/github/codeql-action/blob/9b3254085494b55a0f7c4c2b3d2d52cc712ce370/init/action.yml)
- [Frozen analyze input/output schema](https://github.com/github/codeql-action/blob/9b3254085494b55a0f7c4c2b3d2d52cc712ce370/analyze/action.yml)
- [Official immutable bundle release and asset digests](https://github.com/github/codeql-action/releases/tag/codeql-bundle-v2.27.3)
- [Pinned build-mode extraction working directory](https://github.com/github/codeql-action/blob/9b3254085494b55a0f7c4c2b3d2d52cc712ce370/src/codeql.ts#L657-L672)
- [Database upload/cleanup guard](https://github.com/github/codeql-action/blob/9b3254085494b55a0f7c4c2b3d2d52cc712ce370/src/database-upload.ts)
