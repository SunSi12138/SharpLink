#!/usr/bin/env python3
"""One immutable macOS causal run; raw reports, process outcomes and identity are retained."""
import argparse
import collections
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import hosts

METHOD = 'RuntimeSelectionPolicyShouldComposeWithDynamicTopologyReplacementWithoutExtraDial'
CLASS = 'DynamicEndpointIntegrationTests'
MARKER = 'DYNAMIC_ROUTE_CAUSAL '
TEST_FILE = 'test/SharpLink.IntegrationTests/DynamicEndpointSelectionRuntimeIntegrationTests.cs'
HEARTBEAT = '                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))\n'
ALLOWED_OVERLAY = {TEST_FILE, 'test/SharpLink.IntegrationTests/DynamicEndpointIntegrationTests.cs',
                   'test/SharpLink.IntegrationTests/DynamicEndpointSelectionRuntimeDiagnostics.cs'}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def strict_json(text):
    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result, 'duplicate JSON key: ' + key)
            result[key] = value
        return result
    return json.loads(text, object_pairs_hook=pairs,
                      parse_constant=lambda value: (_ for _ in ()).throw(ValueError(value)))


def read_json(path):
    return strict_json(Path(path).read_text(encoding='utf-8-sig'))

def write_json(path, value):
    Path(path).write_text(json.dumps(value, indent=2) + '\n')


def git(root, *args):
    return subprocess.check_output(['git', '-C', str(root), *args], text=True).strip()


class CampaignAborted(ValueError):
    """An observed timeout/leaked descendant stops all later stages even after cleanup."""


def execute(argv, cwd, directory, label, timeout=180):
    status = dict(argv=list(map(str, argv)), cwd=str(cwd), timeout_seconds=timeout,
                  completed=False, timed_out=False)
    fatal = None
    try:
        code, timed_out = hosts.execute(argv, cwd, dict(os.environ), timeout, directory / (label + '.log'))
        status.update(exit_code=code, completed=not timed_out, timed_out=timed_out,
                      process_group_quiescent=True)
        if timed_out or code == -1:
            fatal = CampaignAborted('timeout or leaked descendant; remaining campaign aborted after group cleanup')
            raise fatal
    except hosts.UnsafeProcessGroup as error:
        fatal = error
        status['unsafe_process_group'] = repr(error)
        raise
    except CampaignAborted as error:
        status['campaign_aborted'] = repr(error)
        raise
    except Exception as error:
        status['launcher_error'] = repr(error)
        raise
    finally:
        try:
            write_json(directory / (label + '.status.json'), status)
        except Exception as error:
            if fatal is not None:
                raise hosts.UnsafeProcessGroup('fatal execution; outcome status could not be retained') from error
            raise
    return status


def watchdog_event(event):
    return (type(event.get('EventId')) is int and event['EventId'] == 1102 and
            event.get('Category') == 'SharpLink.Server.SharpLinkServer' and
            event.get('Message') == 'Client disconnected due to heartbeat timeout.')


def exact_dial_error(record):
    sources = ', '.join(attempt['endpoint'] + '@' + attempt['phase'] for attempt in record['attempts'])
    return ('resolver replacement requires exactly one new dial; policy publications must add none: '
            'expected total=3, first=1, second=1, third=1; '
            f"actual total={record['total']}, first={record['first']}, second={record['second']}, third={record['third']}; "
            f'dial sources=[{sources}]')


def select_cases(document):
    require(type(document.get('schemaVersion')) is int and document['schemaVersion'] == 1, 'unsupported discovery schema')
    tests = document.get('tests')
    require(isinstance(tests, list), 'missing discovered tests')
    all_uids = [test.get('uid') for test in tests]
    require(all(isinstance(uid, str) and uid for uid in all_uids) and len(set(all_uids)) == len(all_uids),
            'missing or duplicate discovery UID')
    selected = [test for test in tests if test.get('type', {}).get('methodName') == METHOD]
    require(len(selected) == 2, 'discovery must identify exactly the two parameterized cases')
    result = {}
    for test in selected:
        identity = test.get('type', {})
        require(identity.get('typeName') == CLASS and identity.get('namespace') == 'SharpLink.IntegrationTests'
                and identity.get('parameterTypeFullNames') == ['System.Int32'], 'wrong discovered method identity')
        match = re.fullmatch(re.escape(METHOD) + r'\((100|700)\)', test.get('displayName', ''))
        require(match is not None, 'parameter display is not an exact reviewed value')
        delay = int(match.group(1))
        require(delay not in result, 'duplicate parameterized case')
        result[delay] = test['uid']
    require(set(result) == {100, 700}, 'missing parameter case')
    return result


def exact_summary(summary, failed):
    expected = dict(total=1, passed=0 if failed else 1, failed=1 if failed else 0,
                    skipped=0, cancelled=0, timedOut=0, flaky=0)
    require(summary == expected and all(type(value) is int for value in summary.values()),
            'report is not one completed expected result: ' + repr(summary))


def validate_report(report, uid, delay, negative, status):
    require(status.get('completed') is True and status.get('timed_out') is False
            and status.get('process_group_quiescent') is True
            and status.get('exit_code') in ((1, 2) if negative else (0,)), 'unexpected process outcome')
    require(type(report.get('schemaVersion')) is int and report['schemaVersion'] == 1 and report.get('assemblyName') == 'SharpLink.IntegrationTests'
            and report.get('tunitVersion') == '1.73.19.0'
            and report.get('runtimeVersion') == '.NET 10.0.14'
            and str(report.get('operatingSystem', '')).startswith('macOS'), 'wrong report identity')
    require(re.fullmatch('[0-9a-f]{32}', report.get('publicationGeneration', '')) is not None,
            'missing final publication generation')
    exact_summary(report.get('summary'), negative)
    groups = report.get('groups')
    require(isinstance(groups, list) and len(groups) == 1 and groups[0].get('className') == CLASS,
            'wrong report groups')
    exact_summary(groups[0].get('summary'), negative)
    tests = groups[0].get('tests')
    require(isinstance(tests, list) and len(tests) == 1, 'wrong executed count')
    test = tests[0]
    require(test.get('id') == uid and test.get('status') == ('error' if negative else 'passed')
            and type(test.get('retryAttempt', 0)) is int and test.get('retryAttempt', 0) == 0
            and not test.get('attempts'), 'wrong UID/status/retry')
    require(test.get('startTime') and test.get('endTime'), 'missing completed test timestamps')
    output = test.get('output', '')
    records = [strict_json(line[len(MARKER):]) for line in output.splitlines() if line.startswith(MARKER)]
    require(len(records) == 1, 'missing or duplicate final diagnostic record')
    record = records[0]
    require(type(record.get('schema')) is int and record['schema'] == 1
            and type(record.get('streamDelayMs')) is int and record['streamDelayMs'] == delay
            and record.get('initialCountsVerified') is True and record.get('policyOnlyCountsVerified') is True,
            'initial topology or no-dial policy baseline was not established')
    attempts = record.get('attempts')
    require(isinstance(attempts, list) and all(set(attempt) == {'endpoint', 'phase'} and
            attempt['endpoint'] in {'first', 'second', 'third'} and isinstance(attempt['phase'], str)
            for attempt in attempts), 'missing physical dial source/phase history')
    actual = collections.Counter(attempt['endpoint'] for attempt in attempts)
    require(all(type(record.get(key)) is int for key in ('total', 'first', 'second', 'third'))
            and record.get('total') == len(attempts) and all(record.get(key) == actual[key]
            for key in ('first', 'second', 'third')), 'inconsistent dial counts')
    events = record.get('serverEvents')
    require(isinstance(events, list), 'missing server event history')
    timeouts = [event for event in events if event.get('EventId') == 1102]
    if negative:
        require(record.get('bodyCompleted') is False and record.get('phase') in
                {'accepted stream drain', 'replacement readiness'}, 'negative did not reach the causal window')
        require(any(event.get('Endpoint') in {'first', 'third'} and watchdog_event(event)
                    for event in timeouts), 'generic failure without an idle sibling heartbeat timeout is not causal')
        error = test.get('exception')
        diagnostic = record.get('error')
        require(isinstance(error, dict) and isinstance(diagnostic, dict) and
                error.get('type') == diagnostic.get('type') and error.get('message') == diagnostic.get('message')
                and METHOD in error.get('stackTrace', ''), 'original exception identity was not preserved')
        dial_failure = (error['type'] == 'System.Exception' and error['message'] == exact_dial_error(record)
            and diagnostic.get('code') is None and record['total'] > 3
            and all(record[key] >= 1 for key in ('first', 'second', 'third'))
            and (record['first'] > 1 or record['third'] > 1))
        interrupted_stream = (error['type'] == 'SharpLink.Abstractions.SharpLinkException' and
                              diagnostic.get('code') == 'Unavailable' and record['phase'] == 'accepted stream drain'
                              and any(event.get('Endpoint') == 'second' and watchdog_event(event) for event in timeouts))
        require(dial_failure or interrupted_stream, 'failure is not the specific observed watchdog/dial mechanism')
    else:
        require(record.get('bodyCompleted') is True and record.get('error') is None
                and record.get('phase') == 'replacement readiness' and not timeouts,
                'positive did not complete with healthy sessions')
        require(record['total'] == 3 and all(record[key] == 1 for key in ('first', 'second', 'third')),
                'policy must add zero dials and replacement exactly one')
        require(not test.get('exception'), 'unexpected positive exception')
        if delay == 700:
            require(record.get('elapsedMs', 0) >= 2100, 'slow producer did not cross the 500 ms watchdog window')
    return record


def provenance(tools_root, positive, negative, pins, out):
    facts = {'tools_commit': git(tools_root, 'rev-parse', 'HEAD'), 'arms': {}}
    if os.environ.get('GITHUB_SHA'):
        require(facts['tools_commit'] == os.environ['GITHUB_SHA'], 'tools checkout is not the request SHA')
    for arm, root in [('positive', positive), ('negative', negative)]:
        require(git(root, 'status', '--porcelain') == '', arm + ' checkout is dirty')
        sha, tree = git(root, 'rev-parse', 'HEAD'), git(root, 'rev-parse', 'HEAD^{tree}')
        require(sha == pins[arm]['sha'] and tree == pins[arm]['tree'], 'wrong frozen ' + arm + ' source')
        facts['arms'][arm] = dict(sha=sha, tree=tree, production_tree=git(root, 'rev-parse', 'HEAD:src'))
    require(facts['arms']['positive']['production_tree'] == facts['arms']['negative']['production_tree'] ==
            pins['production_tree'], 'production code drift')
    require(git(positive, 'rev-parse', 'HEAD^') == pins['candidate'], 'positive overlay not directly based on reviewed candidate')
    require(set(git(positive, 'diff', '--name-only', pins['candidate'], 'HEAD').splitlines()) == ALLOWED_OVERLAY,
            'positive diagnostic overlay has unexpected files')
    require(git(negative, 'rev-parse', 'HEAD^') == pins['positive']['sha'], 'negative not directly based on positive')
    require(git(negative, 'diff', '--name-only', 'HEAD^', 'HEAD') == TEST_FILE, 'negative changes unexpected files')
    good, bad = (positive / TEST_FILE).read_text(), (negative / TEST_FILE).read_text()
    require(good.count(HEARTBEAT) == 1 and bad == good.replace(HEARTBEAT, ''),
            'negative must only remove the one matching client heartbeat configuration')
    for arm, root in [('positive', positive), ('negative', negative)]:
        (out / (arm + '.diff')).write_text(git(root, 'diff', 'HEAD^', 'HEAD') + '\n')
    write_json(out / 'source-identity.json', facts)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--positive', type=Path, required=True)
    parser.add_argument('--negative', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    tools_root = Path(__file__).resolve().parents[2]
    out = args.output.resolve()
    out.mkdir(parents=True, exist_ok=False)
    pins = read_json(Path(__file__).with_name('pins.json'))
    write_json(out / 'pins.json', pins)
    outcomes = []
    try:
        positive, negative = args.positive.resolve(), args.negative.resolve()
        provenance(tools_root, positive, negative, pins, out)
        info = execute(['dotnet', '--info'], tools_root, out, 'dotnet-info')
        require(info['exit_code'] == 0, 'cannot identify runtime')
        for arm, root, delays in [('positive', positive, (100, 700)), ('negative', negative, (700,))]:
            directory = out / arm
            directory.mkdir()
            try:
                build = execute(['dotnet', 'build', 'test/SharpLink.IntegrationTests/SharpLink.IntegrationTests.csproj',
                                 '-c', 'Release', '--disable-build-servers', '-m:1', '-nr:false', '-p:UseSharedCompilation=false'],
                                root, directory, 'build', 600)
                require(build['completed'] and not build['timed_out'] and build['exit_code'] == 0, arm + ' build failed')
                assembly = root / 'test/SharpLink.IntegrationTests/bin/Release/net10.0/SharpLink.IntegrationTests.dll'
                write_json(directory / 'binary-sha256.json', {path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                           for path in sorted(assembly.parent.glob('*.dll'))})
                discovery = execute(['dotnet', str(assembly), '--list-tests', 'json'], root, directory, 'discovery')
                require(discovery['completed'] and not discovery['timed_out'] and discovery['exit_code'] == 0,
                        'discovery process failed')
                selected = select_cases(read_json(directory / 'discovery.log'))
                write_json(directory / 'selected-uids.json', selected)
                for delay in delays:
                    case = directory / str(delay)
                    case.mkdir()
                    outcome = dict(arm=arm, delay=delay, uid=selected[delay], accepted=False)
                    try:
                        status = execute(['dotnet', str(assembly), '--filter-uid', selected[delay],
                            '--maximum-parallel-tests', '1', '--minimum-expected-tests', '1', '--zero-tests-policy', 'strict',
                            '--no-ansi', '--progress', 'off', '--show-stdout', 'None', '--show-stderr', 'None',
                            '--report-html', '--report-html-filename', str(case / 'result.html'),
                            '--results-directory', str(case / 'results'), '--diagnostic', '--diagnostic-verbosity', 'Trace',
                            '--diagnostic-output-directory', str(case)], root, case, 'worker')
                        reports = list(case.glob('*.tunit-report.json'))
                        require(len(reports) == 1, 'missing or ambiguous final test report')
                        record = validate_report(read_json(reports[0]), selected[delay], delay, arm == 'negative', status)
                        outcome.update(accepted=True, diagnostic=record)
                    except (hosts.UnsafeProcessGroup, CampaignAborted) as error:
                        outcome.update(error=repr(error), fatal_execution=True)
                        raise
                    except Exception as error:
                        outcome['error'] = repr(error)
                    finally:
                        outcomes.append(outcome)
                        try:
                            write_json(case / 'oracle.json', outcome)
                        except Exception as error:
                            if outcome.get('fatal_execution'):
                                raise hosts.UnsafeProcessGroup('fatal execution; oracle could not be retained') from error
                            raise
                        print(json.dumps(outcome), flush=True)
            except (hosts.UnsafeProcessGroup, CampaignAborted) as error:
                outcomes.append(dict(arm=arm, stage='fatal-execution', accepted=False,
                                     error=repr(error), fatal_execution=True))
                raise
            except Exception as error:
                outcomes.append(dict(arm=arm, stage='build-or-discovery', accepted=False, error=repr(error)))
    finally:
        write_json(out / 'summary.json', outcomes)
    require(len(outcomes) == 3 and all(outcome['accepted'] for outcome in outcomes),
            'causal gate incomplete or rejected; retain all original evidence')


if __name__ == '__main__':
    main()
