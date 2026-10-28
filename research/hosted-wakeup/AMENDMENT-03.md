# Pre-execution amendment03: explicit known hosted environment normalization

Proposed for independent review before a new prepare-only request. This is a
wrapper/environment-policy change, not a rerun of run40710903027 and not a
zero-change claim. That run's two failed preflights, raw logs and artifacts remain
retained. Neither architecture downloaded .NET, built a product or consumed a
probe cell. The failure was the tool's overly broad inherited-environment gate,
not evidence of a product failure. No collection or performance acceptance exists.

## Exact policy `hosted-known-env-v1`

At entry to both prepare and collect, before any download/restore/command,
record the original relevant environment (all DOTNET/COMPlus/CoreCLR/CoreHost/
COR/MONO/LD/DYLD keys, plus PATH, pythonLocation and RUNNER_TOOL_CACHE), not
unrelated token/secret variables. Retain this record even if validation fails.
Make a separate environment copy. Only these two exact cases may be removed:

1. `DOTNET_MULTILEVEL_LOOKUP=0`. Absence is also allowed. Empty string, `1`,
   `false`, whitespace, alternative casing and unknown .NET overrides are not
   accepted. Its removal applies identically to both architectures and D/C.
2. Linux only: `LD_LIBRARY_PATH=/opt/hostedtoolcache/Python/3.12.15/x64/lib`,
   with `pythonLocation=/opt/hostedtoolcache/Python/3.12.15/x64`,
   `RUNNER_TOOL_CACHE=/opt/hostedtoolcache`, actual `sys.version_info`3.12.15,
   `sys.prefix` equal to that installation and `sys.executable` in its `bin`.
   No additional path, trailing colon/slash, empty value, other Python version,
   other location, macOS LD setting or DYLD equivalent is allowed.

Record the exact removals and resulting relevant environment. Never mutate
`os.environ`, never wildcard-delete prefixes, and pass the result explicitly to
**every wrapper-launched command**, including dotnet --info, builds, decoder and
Python collector. Missing explicit environment is itself a launch error. The
original native collector's strict gate is unchanged and accepts the normalized
input; no loader/tier/profiler exception reaches a product process. Unknown
settings remain fatal before download, restore or managed launch, with no scrub
and retry. Host information commands are non-CLR observations of the original
machine; no claim is made that the Python driver started without the known loader
path (setup-python needs its own environment).

The workflow pins the Python patch versions actually observed in first-run logs:
Linux3.12.15; macOS3.12.10. Standard runner labels, action SHAs and permissions are
unchanged. Prepare performs exactly one additional normalized **Python-only**
startup check before archive download, retaining its command/stdout/exit status.
This checks that the embedded Python rpath works without LD_LIBRARY_PATH; failure
is terminal, not a reason to restore the loader path and retry. No .NET/probe
warmup or cell is added. Collect performs no extra probe, build or Python selftest.

## Official basis and limits of the first observation

- [Official runner-image Ubuntu .NET installer, pinned source](https://github.com/actions/runner-images/blob/09839e8f4ae2340e6a08234ba06d0d945676165a/images/ubuntu/scripts/build/install-dotnetcore-sdk.sh)
  sets DOTNET_MULTILEVEL_LOOKUP to exact0. The first failed jobs recorded the key
  name, not its value; therefore **do not claim its historical value was measured**
  on either machine. The next run must record and check the actual value before
  proceeding. The same0 policy is permitted on macOS, not an assertion of an
  uninspected macOS provisioning script.
- [Official .NET environment-variable reference](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-environment-variables#dotnet_multilevel_lookup)
  limits this setting to net6 and earlier applications; net7+ searches one framework
  location. Thus removing known0 does not change framework multi-level lookup for
  our net10 SDK/probe/decoder. This is not a general claim about other .NET settings.
  Installed archive/member checks and actual host14/architecture checks still apply.
- [Exact pinned setup-python source](https://github.com/actions/setup-python/blob/83679a892e2d95755f2dac6acb0bfd1e9ac5d548/src/find-python.ts)
  exports `pythonLocation` and prepends that installation's lib directory to
  LD_LIBRARY_PATH on Linux. First-run Linux raw logs actually show the exact
  single path above. Additional inherited paths are possible in general but are
  deliberately rejected here, not accepted by a prefix wildcard.
- [Official Python Ubuntu builder, pinned source](https://github.com/actions/python-versions/blob/160ca1e232997f36def6bb6e857f628222bef58a/builders/ubuntu-python-builder.psm1)
  embeds the full toolcache lib path in the linker rpath when building shared
  Python. This motivates (but does not replace) the actual normalized launch check.
  LD_LIBRARY_PATH is a native-loader search input, **not** an ignored net10 setting:
  we remove the verified known path from all child environments to avoid allowing
  Python's native libraries to influence CLR dependency resolution.

## New request, not replacement samples

Both prepare and collect REQUEST schemas now require exactly
`environmentPolicy: hosted-known-env-v1`. This semantic field change intentionally
triggers the existing prepare branch's REQUEST-only push filter after independent
review and root authorization. GITHUB_RUN_ATTEMPT must still be1. No rerun API,
new runner class, extra permission or paid expansion is introduced. New true tool
commit/tree and fresh builds/receipts are recorded; the old failed run is never
relabeled. Independent hosted receipt review is still required before collect.

Native plan/hash/lock, source D/C, runtime10.0.14, workloads, natural-tier settings,
8-cell per-architecture fail-stop schedules, raw caps, failure preservation and
no-retry/no-replacement rules remain unchanged. No QPS/P99 benchmark or target
claim is derived from this repair.
