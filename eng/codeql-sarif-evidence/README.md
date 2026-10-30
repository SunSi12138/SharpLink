# CQL1 — isolated full-source CodeQL SARIF evidence

**A local review candidate; no authorization is implied by this document.** Release
requires independent review of the exact tool tree, root release, and one request
child. The dormant `REQUEST.json` does not authorize execution.

## Scope and budget

- Analyze product commit `c22b945f71c075c878e93a7c92bf4e45a8d33a6f`, exact tree
  `12b88cb38821e2c40bd1fb209a6244396bed52ba`, in a separate clean `source/` checkout.
  The workflow/wrapper lives in `tools/`; its reviewed parent and request identities
  are recorded separately. Before/after tracked source hashes must agree.
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

## Evidence and interpretation

The temporary artifact (3-day retention) explicitly allowlists raw `csharp.sarif`,
request/source/toolchain identity, query-result hashes, action outcomes, findings,
completion/incomplete status and artifact hashes. It never broadly uploads the
runner temp directory, source checkout, cache, CodeQL database, environment,
configuration, or credentials. Original SARIF bytes are copied before campaign
completeness checks; failures retain whatever evidence exists and fail the job.

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
   `validation/codeql-sarif-c22-20261030-r1`; read back tree and parent.
3. Create one child changing only `eng/codeql-sarif-evidence/REQUEST.json`, with
   fixed message `run: isolated CodeQL SARIF CQL1`. Fill exact remote reviewed
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
- [Database upload/cleanup guard](https://github.com/github/codeql-action/blob/9b3254085494b55a0f7c4c2b3d2d52cc712ce370/src/database-upload.ts)
