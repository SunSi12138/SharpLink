"""Synthetic fixtures only: never run perf, .NET, RPCs, builds or the network."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location('cpu_analyze', Path(__file__).with_name('analyze.py'))
a = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(a)


def sample(time='30.000000001', frames=None, period=100, pid=42, tid=43, event='task-clock:u'):
    frames = ['StartServerStreamingInvokerAsync'] if frames is None else frames
    prefix = '' if period is None else f'{period} '
    return f'dotnet worker {pid}/{tid} {time}: {prefix}{event}:\n' + ''.join(
        f'    {index + 16:x} {symbol} (/tmp/perf-42.map)\n' for index, symbol in enumerate(frames)) + '\n'


class Fixture(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / 'decoded').mkdir()
        names = ['process-start', 'warmup-start', 'warmup-end', 'observation-start', 'observation-end', 'process-end']
        times = [1, 2, 29, 30, 50, 60]
        self.phases = [dict(Name=name, Completed=10 if name.endswith('-end') else 0,
                            MonotonicNs=int(time * 1e9), CpuReadEndNs=int(time * 1e9) + 100,
                            UserUs=index * 1000, SystemUs=index * 100, TotalUs=index * 1100, Pid=42)
                       for index, (name, time) in enumerate(zip(names, times))]
        self.events = [dict(provider='SharpLink-RpcCpu-Phase', name='Boundary', pid=42, tid=43,
                            timeMs=p['MonotonicNs'] / 1e6 + 0.0001,
                            payload=dict(name=p['Name'], count=str(p['Completed']),
                                         monotonicNs=str(p['MonotonicNs']), cpuReadEndNs=str(p['CpuReadEndNs'])))
                       for p in self.phases]
        self.coverage = dict(eventsLost=0, parsedEvents=6, providers={'SharpLink-RpcCpu-Phase': 6})
        self.identity = {'pid': 42}
        self.result = {'processId': 42, 'operations': 10, 'complete': True}
        self.perf = sample()
        self.stderr = ''

    def run_analysis(self):
        for file, rows in [('phases.jsonl', self.phases), ('decoded/events.jsonl', self.events)]:
            (self.root / file).write_text(''.join(json.dumps(x) + '\n' for x in rows))
        (self.root / 'pid.json').write_text(json.dumps(self.identity))
        (self.root / 'result.json').write_text(json.dumps(self.result))
        (self.root / 'decoded/coverage.json').write_text(json.dumps(self.coverage))
        (self.root / 'perf-script.txt').write_text(self.perf)
        (self.root / 'perf-script.stderr').write_text(self.stderr)
        (self.root / 'record.stderr').write_text('')
        report = a.analyze(self.root)
        json.dumps(report, allow_nan=False)
        return report

    def test_process_cpu_and_half_open_interval(self):
        self.perf = ''.join(sample(time) for time in ['1.5', '2.5', '29.999999999', '30.000000000', '49.999999999', '50.000000000'])
        r = self.run_analysis()
        self.assertEqual(r['errors'], [])
        self.assertEqual(r['sample_counts_by_phase'], dict(startup=1, warmup=2, observation=2, teardown=1, unclassified=0))
        self.assertEqual(r['process_cpu']['observation']['cpu_us_per_rpc'], dict(UserUs=100, SystemUs=10, TotalUs=110))
        self.assertEqual(r['observation']['inclusive_family_presence']['bootstrap']['estimated_user_cpu_us_per_rpc'], 100)

    def test_inclusive_overlap_and_disjoint_nearest_leaf(self):
        self.perf = sample(frames=['System.Threading.CancellationTokenSource.Cancel',
                                   'FrameworkTaskSupervisor.Observe', 'StartServerStreamingInvokerAsync'])
        r = self.run_analysis()['observation']
        for name in ('bootstrap', 'supervision', 'cancellation'):
            self.assertEqual(r['inclusive_family_presence'][name]['samples'], 1)
        self.assertEqual(r['disjoint_nearest_leaf_family']['cancellation']['samples'], 1)
        self.assertEqual(sum(v['samples'] for v in r['disjoint_nearest_leaf_family'].values()), 1)
        self.assertIsNone(r['disjoint_nearest_leaf_family']['bootstrap']['estimated_user_cpu_us_per_rpc'])

    def test_period_fraction_keeps_unknown_denominator(self):
        self.perf = sample(period=100) + sample(period=300, frames=['[unknown]'])
        r = self.run_analysis()['observation']
        self.assertEqual(r['weight_basis'], 'period_fraction')
        self.assertEqual(r['weight_denominator'], 400)
        self.assertEqual(r['inclusive_family_presence']['bootstrap']['fraction_of_all_observation_samples'], .25)
        self.assertEqual(r['inclusive_family_presence']['bootstrap']['estimated_user_cpu_us_per_rpc'], 25)
        self.assertEqual(r['disjoint_nearest_leaf_family'][a.OTHER]['samples'], 1)
        self.assertEqual(r['unresolved_samples'], 1)

    def test_missing_period_uses_sample_fraction_for_entire_set(self):
        self.perf = sample(period=None) + sample(period=300, frames=['unmatched_native_symbol'])
        r = self.run_analysis()['observation']
        self.assertEqual(r['weight_basis'], 'sample_fraction')
        self.assertEqual(r['weight_denominator'], 2)
        self.assertEqual(r['inclusive_family_presence']['bootstrap']['fraction_of_all_observation_samples'], .5)

    def test_empty_stack_stays_in_denominator_and_no_zero_inference(self):
        self.perf = sample(frames=[])
        r = self.run_analysis()['observation']
        self.assertEqual(r['sample_count'], 1)
        self.assertEqual(r['empty_stack_samples'], 1)
        self.assertEqual(r['disjoint_nearest_leaf_family'][a.OTHER]['samples'], 1)
        self.assertIsNone(r['inclusive_family_presence']['bootstrap']['estimated_user_cpu_us_per_rpc'])
        self.assertEqual(r['inclusive_family_presence']['bootstrap']['status'], 'not_observed_not_zero')

    def test_truncation_threshold_64(self):
        self.perf = sample(frames=['native'] * 63) + sample(frames=['native'] * 64)
        r = self.run_analysis()
        self.assertEqual(r['observation']['potentially_truncated_samples_ge_64_frames'], 1)
        self.assertTrue(any('64-frame limit' in x for x in r['warnings']))

    def test_unknown_lines_and_stderr_retained(self):
        self.perf = sample().rstrip() + '\n unreadable stack fragment\n\nunknown header\n'
        self.stderr = 'warning: unknown diagnostic\nWarning: lost 7 samples\n'
        r = self.run_analysis()
        self.assertEqual(len(r['parser_coverage']['unknown_lines']), 2)
        self.assertIn(self.stderr, r['parser_coverage']['stderr'])
        self.assertEqual(r['observation']['samples_with_unparsed_stack_lines'], 1)
        self.assertEqual(r['parser_coverage']['lost_sample_count'], 7)

    def test_duplicate_lost_reports_not_summed_and_absence_unknown(self):
        p = a.parse_perf('lost 5 samples\n', 'lost 5 samples\n')
        self.assertEqual(p['lost_sample_count'], 5)
        self.assertEqual(p['reported_lost_sample_counts'], [5, 5])
        self.assertIsNone(a.parse_perf('')['lost_sample_count'])

    def test_jit_nonstationarity_retained_no_filter(self):
        self.events.extend([dict(provider='Microsoft-Windows-DotNETRuntime', name='Method/JittingStarted',
                                 timeMs=40000, pid=42, payload={'MethodName': 'M'}),
                            dict(provider='Microsoft-Windows-DotNETRuntime', name='TieredCompilation/BackgroundJitStop',
                                 timeMs=45000, pid=42, payload={})])
        r = self.run_analysis()
        self.assertEqual(r['runtime']['stationarity'], 'nonstationary_jit_or_code_transition_observed')
        self.assertEqual(len(r['runtime']['observation_jit_or_tier_transition_events']), 2)
        self.assertEqual(r['observation']['sample_count'], 1)
        self.assertEqual(r['errors'], [])

    def test_no_transition_is_not_stationarity_proof(self):
        r = self.run_analysis()
        self.assertEqual(r['runtime']['stationarity'], 'not_established_no_transition_observed')

    def test_phase_marker_mismatch_suppresses_estimate(self):
        self.events[3]['payload']['monotonicNs'] = '999999'
        r = self.run_analysis()
        self.assertTrue(any('disagreement' in x for x in r['errors']))
        self.assertIsNone(r['observation']['inclusive_family_presence']['bootstrap']['estimated_user_cpu_us_per_rpc'])
        self.assertEqual(r['observation']['sample_count'], 1)

    def test_runtime_clock_offset_is_not_absolute_time_confusion(self):
        for event in self.events:
            event['timeMs'] -= 900
        self.events.append(dict(provider='Microsoft-Windows-DotNETRuntime', name='GC/Start', timeMs=39100, pid=42, payload={}))
        r = self.run_analysis()['runtime']
        self.assertAlmostEqual(r['clock_mapping']['offset_ns'], 899999900, delta=1)
        self.assertEqual(r['event_counts_by_phase']['observation']['GC/Start'], 1)

    def test_foreign_process_and_wrong_event_samples_excluded_but_retained(self):
        self.perf += sample(pid=9) + sample(event='cycles:u')
        r = self.run_analysis()
        self.assertEqual(r['observation']['sample_count'], 1)
        self.assertEqual(len(r['excluded_samples']), 2)

    def test_runtime_unknown_and_allocation_retained(self):
        self.events += [dict(provider='Unexpected', name='Mystery', pid=42, timeMs=35000, payload={}),
                        dict(provider='Microsoft-Windows-DotNETRuntime', name='GC/AllocationTick', pid=42, timeMs=35001, payload={})]
        r = self.run_analysis()['runtime']
        self.assertEqual(len(r['unrecognized_events']), 1)
        self.assertEqual(len(r['unexpected_allocation_events']), 1)
        self.assertEqual(len(r['mapped_events']), 2)

    def test_invalid_cpu_delta_preserves_raw_and_suppresses_per_rpc(self):
        self.phases[4]['UserUs'] = 0
        r = self.run_analysis()
        self.assertFalse(r['process_cpu']['observation']['valid'])
        self.assertIsNone(r['process_cpu']['observation']['cpu_us_per_rpc']['UserUs'])

    def test_missing_input_does_not_become_zero_measurement(self):
        r = a.analyze(self.root)
        self.assertTrue(r['errors'])
        self.assertIsNone(r['observation']['inclusive_family_presence']['bootstrap']['estimated_user_cpu_us_per_rpc'])
        self.assertIsNone(r['process_cpu']['observation'])

    def test_precision_above_float_exact_integer_range(self):
        p = a.parse_perf(sample(time='123456789.123456789'))
        self.assertEqual(p['samples'][0]['time_ns'], 123456789123456789)
        self.assertEqual(p['samples'][0]['comm'], 'dotnet worker')

    def test_split_pid_tid_and_inline_frame(self):
        p = a.parse_perf('dotnet 42 43 3.000000001: 99 task-clock:u: abc StartServerStreamingInvokerAsync+0x10 (/tmp/a b)\n\n')
        self.assertEqual(len(p['samples']), 1)
        self.assertEqual(p['samples'][0]['period'], 99)
        self.assertEqual(p['samples'][0]['frames'][0]['dso'], '/tmp/a b')

    def test_phase_duplicate_is_error(self):
        self.phases.insert(4, self.phases[3])
        r = self.run_analysis()
        self.assertTrue(any('duplicate phase' in x for x in r['errors']))

    def test_subnanosecond_timestamp_retained_not_crashed(self):
        parsed = a.parse_perf(sample(time='123.1234567891'))
        self.assertEqual(parsed['samples'], [])
        self.assertEqual(parsed['unknown_lines'][0]['kind'], 'invalid_timestamp')

    def test_lost_events_are_not_claimed_as_lost_samples(self):
        parsed = a.parse_perf('Warning: lost 8 events\n')
        self.assertEqual(parsed['lost_event_count'], 8)
        self.assertIsNone(parsed['lost_sample_count'])

    def test_zero_observation_is_error(self):
        self.perf = sample(time='1.5')
        r = self.run_analysis()
        self.assertTrue(any('No parsed target-process observation samples' in x for x in r['errors']))

    def test_short_observation_rejected(self):
        self.phases[4]['MonotonicNs'] -= 1
        self.phases[4]['CpuReadEndNs'] -= 1
        self.events[4]['payload']['monotonicNs'] = str(self.phases[4]['MonotonicNs'])
        self.events[4]['payload']['cpuReadEndNs'] = str(self.phases[4]['CpuReadEndNs'])
        r = self.run_analysis()
        self.assertTrue(any('observation duration below' in x for x in r['errors']))

    def test_result_operations_and_pid_mismatch_rejected(self):
        self.result['operations'] = 11
        self.result['processId'] = 2
        r = self.run_analysis()
        self.assertTrue(any('operations' in x for x in r['errors']))
        self.assertTrue(any('process ID disagreement' in x for x in r['errors']))

    def test_loss_is_error_and_suppresses_scaled_estimates(self):
        self.coverage['eventsLost'] = 1
        self.stderr = 'lost 2 samples\n'
        r = self.run_analysis()
        self.assertTrue(any('Loss reported' in x for x in r['errors']))
        self.assertIsNone(r['observation']['inclusive_family_presence']['bootstrap']['estimated_user_cpu_us_per_rpc'])

    def test_noncomment_unknown_record_is_error(self):
        self.perf += 'not a recognized sample header\n'
        r = self.run_analysis()
        self.assertTrue(any('Unparsed nonblank' in x for x in r['errors']))

    def test_comment_only_not_parse_error(self):
        self.perf += '# perf comment\n'
        self.assertEqual(self.run_analysis()['errors'], [])

    def test_unexpected_phase_is_error(self):
        extra = dict(self.phases[-1], Name='replacement-start')
        self.phases.append(extra)
        r = self.run_analysis()
        self.assertTrue(any('unexpected phase names' in x for x in r['errors']))

    def test_unexpected_allocation_event_is_error(self):
        self.events.append(dict(provider='Microsoft-Windows-DotNETRuntime', name='GC/AllocationTick', pid=42, timeMs=35001, payload={}))
        r = self.run_analysis()
        self.assertTrue(any('Unexpected allocation events' in x for x in r['errors']))

    def test_method_load_is_transition_evidence(self):
        self.events.append(dict(provider='Microsoft-Windows-DotNETRuntime', name='Method/LoadVerbose', pid=42, timeMs=35000, payload={'OptimizationTier': 'Tier1'}))
        r = self.run_analysis()
        self.assertEqual(r['errors'], [])
        self.assertEqual(r['runtime']['stationarity'], 'nonstationary_jit_or_code_transition_observed')

    def test_cli_writes_report_and_returns_two_on_zero_samples(self):
        self.run_analysis()
        (self.root / 'perf-script.txt').write_text('')
        output = self.root / 'analysis.json'
        with patch('sys.argv', ['analyze.py', str(self.root), '--output', str(output)]):
            self.assertEqual(a.main(), 2)
        report = json.loads(output.read_text())
        self.assertEqual(report['status'], 'incomplete_or_invalid_inputs')
        self.assertIsNone(report['observation']['inclusive_family_presence']['bootstrap']['estimated_user_cpu_us_per_rpc'])

    def test_cli_writes_valid_report_and_returns_zero(self):
        self.run_analysis()
        output = self.root / 'analysis.json'
        with patch('sys.argv', ['analyze.py', str(self.root), '--output', str(output)]):
            self.assertEqual(a.main(), 0)
        self.assertEqual(json.loads(output.read_text())['errors'], [])

    def test_all_expected_family_patterns(self):
        for symbol, expected in [('PooledAsyncStreamDispatcher<T>.MoveNextAsync', 'dispatcher'),
                                  ('LogicalInvocationAsyncEnumerable<T>.Enumerator.MoveNextAsync', 'logical_wrapper'),
                                  ('SharpLinkCallContext.Push', 'server_context'),
                                  ('ContinuationTaskFromTask.InnerInvoke', 'supervision')]:
            self.assertEqual(a.family_for(symbol), expected)


if __name__ == '__main__':
    unittest.main()
