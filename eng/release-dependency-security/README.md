# Frozen source dependency security acceptance

A single request-only child commit of reviewed tools triggers one hosted scan.
No product changes, builds, tests, package publishing, audit overrides, credentials,
cache restore, or additional permissions. Tools and exact source checkouts are separate.
Original `global.json` selects SDK (10.0.102 with latestPatch), and original audit /
warnings-as-errors policy remains in effect. Empty package and HTTP cache paths are
asserted before normal solution restore. Restore failure is retained and makes the
result incomplete; available assets and official no-restore diagnostics are still collected.
A command timeout instead kills its isolated process group, records whether group
absence is confirmed, and unconditionally stops further restore/scan work.
JSON report parameters must exactly match the intended mode without extra filters;
malformed source lists and malformed advisory/deprecation entries are incomplete.

Scope: all 64 `Sharplink.slnx` projects, explicitly 10 production `src` projects and
54 development projects. All 33 tracked projects outside that solution are listed
as NOT SCANNED (mobile/workload, package-install, historical fixtures, tooling).
A clean result is not a whole-repository or platform-specific runtime/RID guarantee.

The SDK's own `dotnet package list --help` must expose the selected options before
execution. Official syntax: https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-package-list
JSON v1 spec: https://github.com/NuGet/Home/wiki/%5BSpec%5D-Machine-readable-output-for-dotnet-list-package
All three reports use `--project Sharplink.slnx --no-restore --include-transitive
--format json --output-version 1`, with separate `--vulnerable` and `--deprecated`.
Filtered reports may omit empty frameworks; exact project coverage, successful
unfiltered inventory and fresh assets establish what was scanned, so an empty or
partial document cannot pass. Original stdout/stderr, argv/exit codes, SDK/runtime,
source identity, assets package inventory and summary are uploaded for three days.
Every vulnerability requires human triage; the script fails rather than allowing
an automatic risk waiver. Deprecation is separately reported, not called a vulnerability.
No known advisories means only the current configured feed reported none.

Local test: `python3 -m unittest discover -s eng/release-dependency-security -p 'test_*.py' -v`.
Tests use only synthetic JSON and Git fixtures; actual restore/scan is hosted-only.
