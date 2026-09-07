#!/usr/bin/env python3
from pathlib import Path
import sys

root = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path.cwd()


def replace_once(relative, old, new):
    path = root / relative
    text = path.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{relative}: expected exactly one replacement target, found {count}")
    path.write_text(text.replace(old, new), encoding="utf-8")


replace_once(
    "src/SharpLink.Client/PendingRequestTable.cs",
    '''    private void ScanExpiredDeadlines()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var slots = Volatile.Read(ref _slots);
        if (slots is null)
            return;

        for (var index = 0; index < slots.Length; index++)
        {
            var call = Volatile.Read(ref slots[index]);
            if (call is null || !call.Deadline.HasValue)
                continue;
            if (call.Deadline.IsExpired(_timeProvider))
            {
                TryComplete(call.Id, PendingCallCompletionReason.DeadlineExceeded);
            }
            else
            {
                _deadlineScheduler.Observe(call.Deadline);
            }
        }
    }
''',
    '''    private bool TryTakeExpiredCallAtIndex(
        PendingCall?[] slots,
        int index,
        PendingCall expected,
        long expectedId,
        out PendingCall? call)
    {
        lock (expected.CompletionGate)
        {
            if (!ReferenceEquals(Volatile.Read(ref slots[index]), expected) ||
                expected.Id != expectedId ||
                !expected.Deadline.HasValue ||
                !expected.Deadline.IsExpired(_timeProvider))
            {
                call = null;
                return false;
            }

            if (!ReferenceEquals(Interlocked.CompareExchange(ref slots[index], null, expected), expected))
            {
                call = null;
                return false;
            }

            expected.WaitUntilRegistered();
            call = expected;
            return true;
        }
    }

    private void ScanExpiredDeadlines()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var slots = Volatile.Read(ref _slots);
        if (slots is null)
            return;

        for (var index = 0; index < slots.Length; index++)
        {
            var call = Volatile.Read(ref slots[index]);
            if (call is null)
                continue;

            // This first sample is only a cheap candidate filter. The request identity,
            // authoritative deadline check and slot removal are revalidated together below.
            var expectedId = call.Id;
            var deadline = call.Deadline;
            if (!deadline.HasValue)
                continue;
            if (deadline.IsExpired(_timeProvider))
            {
                if (!TryTakeExpiredCallAtIndex(slots, index, call, expectedId, out var expiredCall))
                    continue;

                var emptyPayload = ReadOnlySequence<byte>.Empty;
                CompleteTakenCall(
                    expiredCall!, PendingCallCompletionReason.DeadlineExceeded, exception: null, ref emptyPayload);
            }
            else
            {
                _deadlineScheduler.Observe(deadline);
            }
        }
    }
''')

replace_once(
    "eng/validate-pending-lifecycle.py",
    '"""Isolated baseline characterization OR correct-invariant checks. No production fixes.\n',
    '"""Isolated baseline characterization or selected correct-invariant regression checks.\n')
replace_once(
    "eng/validate-pending-lifecycle.py",
    '''    parser.add_argument("--mode", choices=("characterize", "regression"), default="regression")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/validation/pending")
    args = parser.parse_args()
''',
    '''    parser.add_argument("--mode", choices=("characterize", "regression"), default="regression")
    parser.add_argument("--scenario", action="append", choices=SCENARIOS,
                        help="Run only the selected scenario; may be repeated.")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/validation/pending")
    args = parser.parse_args()
''')
replace_once(
    "eng/validate-pending-lifecycle.py",
    '''    reports = []
    failed = False
    for scenario in SCENARIOS:
''',
    '''    selected_scenarios = tuple(args.scenario) if args.scenario else SCENARIOS
    reports = []
    failed = False
    for scenario in selected_scenarios:
''')
replace_once(
    "eng/validate-pending-lifecycle.py",
    '''    summary = dict(mode=args.mode, baseline="acb160faa72a07835b01d049a2fbcf9070b061df",
                   note="Characterization PASS means exact baseline bugs reproduced, NOT correctness PASS.",
                   reports=reports)
''',
    '''    summary = dict(mode=args.mode, baseline="acb160faa72a07835b01d049a2fbcf9070b061df",
                   scenarios=selected_scenarios,
                   note="Characterization PASS means exact baseline bugs reproduced, NOT correctness PASS.",
                   reports=reports)
''')

replace_once(
    "docs/validation/pending-lifecycle.md",
    '''# Pending lifecycle validation — tests only

Issues: #556, #557. Production baseline: `dev@acb160faa72a07835b01d049a2fbcf9070b061df`.

No production source, public API, wire format, or policy is changed. This PR does not close the issues and must not be described as a fix.
''',
    '''# Pending lifecycle validation — #556 fix + #557 evidence

Issues: #556, #557. Characterization baseline: `dev@acb160faa72a07835b01d049a2fbcf9070b061df`.

#556 now carries its minimal production fix in this validation PR. #557 remains characterization/evidence only. No public API, wire format, pool topology, or global synchronization policy is changed; neither issue is auto-closed by this PR.
''')
replace_once(
    "docs/validation/pending-lifecycle.md",
    '''```sh
dotnet build test/SharpLink.UnitTests -c Release
python3 eng/validate-pending-lifecycle.py --mode characterize
python3 eng/validate-pending-lifecycle.py --mode regression
```

The default is **regression**, checking correct invariants. The CI step explicitly selects **characterize**: green means healthy controls pass AND each precise suspected baseline failure is observed. It does NOT mean the invariants pass. Every scenario's `invariant` is recorded in `artifacts/validation/pending/summary.json`. Startup, build, filtering, worker exceptions, and unarmed timeouts are infrastructure failures, never positive reproductions. The characterization gate will fail after a fix until its expectations are intentionally updated.
''',
    '''```sh
dotnet build test/SharpLink.UnitTests -c Release
# #556: the three deterministic deadline-reuse scenarios must now satisfy the correct invariant.
python3 eng/validate-pending-lifecycle.py --mode regression \\
  --scenario deadline-response --scenario deadline-cancel --scenario deadline-disconnect
# #557 and controls remain characterization evidence in this PR.
python3 eng/validate-pending-lifecycle.py --mode characterize \\
  --scenario no-listener --scenario metric-control --scenario metric-minus \\
  --scenario metric-plus --scenario logger-control --scenario logger-throw
```

The default is **regression**, checking correct invariants. CI now runs #556's three deadline scenarios in regression mode while the remaining #557 scenarios stay in characterize mode. Every scenario's `invariant` is recorded in its evidence directory. Startup, build, filtering, worker exceptions, and unarmed timeouts are infrastructure failures, never positive reproductions.
''')
replace_once(
    "docs/validation/pending-lifecycle.md",
    '''This is an investigation harness tied to the existing synchronization boundary. A later fix that moves IsExpired under CompletionGate can legitimately prevent the competing completion from progressing while this gate is held. Such a change requires adapting the coordination to the new boundary, not interpreting a fixture timeout as another reproduction. The driver rejects unarmed timeouts.
''',
    '''The #556 fix intentionally keeps the first deadline sample as a non-authoritative candidate filter, so this deterministic barrier still forces the original A -> B object-reuse interleaving. Before a timeout is actually committed, the scanner enters the existing CompletionGate, revalidates the slot reference and captured request ID, rechecks the current deadline, and only then removes the slot. Therefore the same fixture now proves the ABA is rejected: B stays pending and completes from its own response. The driver still rejects unarmed timeouts.
''')

(root / ".github/workflows/pending-validation.yml").write_text('''name: Pending and codec validation

on:
  pull_request:
    branches: [dev]
    paths:
      - 'src/SharpLink.Client/PendingRequestTable.cs'
      - 'test/SharpLink.UnitTests/Validation/**'
      - 'eng/validate-pending-lifecycle.py'
      - 'eng/validate-codec-semantics.py'
      - '.github/workflows/pending-validation.yml'
  workflow_dispatch:

permissions:
  contents: read

concurrency:
  group: pending-validation-${{ github.event.pull_request.number || github.ref }}
  cancel-in-progress: true

jobs:
  issue-556-regression:
    runs-on: ubuntu-latest
    timeout-minutes: 20
    env:
      TESTINGPLATFORM_TELEMETRY_OPTOUT: '1'
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
        with:
          persist-credentials: false
      - uses: ./.github/actions/setup-dotnet
      - name: Record merge-ref regression provenance
        run: |
          mkdir -p artifacts/validation/issue-556
          git rev-parse HEAD | tee artifacts/validation/issue-556/commit.txt
          dotnet --info > artifacts/validation/issue-556/dotnet-info.txt
      - name: Build test workers with issue 556 fix
        run: dotnet build test/SharpLink.UnitTests -c Release -v minimal
      - name: Validate issue 556 deadline reuse invariant
        run: >-
          python3 eng/validate-pending-lifecycle.py --mode regression
          --scenario deadline-response
          --scenario deadline-cancel
          --scenario deadline-disconnect
          --output artifacts/validation/issue-556/pending
      - name: Upload issue 556 regression evidence
        if: always()
        uses: actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a # v7.0.1
        with:
          name: issue-556-regression-evidence
          path: artifacts/validation/issue-556
          if-no-files-found: warn

  characterize-remaining:
    runs-on: ubuntu-latest
    timeout-minutes: 25
    env:
      TESTINGPLATFORM_TELEMETRY_OPTOUT: '1'
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
        with:
          ref: ${{ github.event.pull_request.head.sha || github.sha }}
          persist-credentials: false
      - uses: ./.github/actions/setup-dotnet
      - name: Record exact characterization provenance
        run: |
          mkdir -p artifacts/validation
          git rev-parse HEAD | tee artifacts/validation/commit.txt
          dotnet --info > artifacts/validation/dotnet-info.txt
      - name: Build test workers
        run: dotnet build test/SharpLink.UnitTests -c Release -v minimal
      - name: Validate remaining issue 557 baseline failures and healthy controls
        id: pending
        run: >-
          python3 eng/validate-pending-lifecycle.py --mode characterize
          --scenario no-listener
          --scenario metric-control
          --scenario metric-minus
          --scenario metric-plus
          --scenario logger-control
          --scenario logger-throw
          --output artifacts/validation/pending
      - name: Validate DateTime semantics and measure fragmented DateTimeOffset collections
        if: ${{ !cancelled() && steps.pending.outcome != 'skipped' }}
        run: python3 eng/validate-codec-semantics.py --mode characterize
      - name: Upload complete remaining characterization evidence
        if: always()
        uses: actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a # v7.0.1
        with:
          name: pending-and-codec-evidence
          path: artifacts/validation
          if-no-files-found: warn
''', encoding="utf-8")
