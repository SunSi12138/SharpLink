import copy
import importlib.util
import json
from pathlib import Path
import sys
import unittest

HERE = Path(__file__).resolve().parent
SOURCE = HERE.parent
sys.path.insert(0, str(SOURCE))
from logical_forwarder_validation import validate_logical_path
spec = importlib.util.spec_from_file_location('request_guard', HERE / 'verify_request.py')
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)


class Protocol(unittest.TestCase):
    def request(self):
        return dict(state='authorized', campaign='logical-forwarder-d17-48tcp-v1', reviewedToolCommit='a'*40,
                    reviewedToolTree='b'*40, baseline=guard.BASELINE, candidate=guard.CANDIDATE, candidateTree=guard.CANDIDATE_TREE)

    def check_request(self, data=None, **kw):
        args = dict(environment=dict(GITHUB_EVENT_NAME='push', GITHUB_REF=guard.BRANCH, GITHUB_RUN_ATTEMPT='1',
                                     GITHUB_REPOSITORY='SunSi12138/SharpLink', GITHUB_SHA='c'*40),
                    head='c'*40, parent='a'*40, parent_tree='b'*40, changed=[guard.REQUEST], message=guard.MESSAGE, clean=True)
        args.update(kw)
        guard.validate_request(data or self.request(), **args)

    def path(self, completion=False, dynamic=False):
        owner = 'CompletionOwnedLogicalInvocationAsyncEnumerable' if completion else 'LogicalInvocationAsyncEnumerable'
        return dict(mode='dynamic-intercepted' if dynamic else 'static-intercepted', wrapperTypes=['SharpLink.Client.SharpLinkClient+'+owner+'`1[System.Int32]'],
                    logicalPathProof=dict(clientActivityListeners=False, clientCallsEnabled=False, telemetryWrapperObserved=False,
                                          completionOwnedObserved=completion, generalLogicalObserved=not completion, observedBeforeWarmup=True))

    def test_valid_request(self): self.check_request()
    def test_wrong_parent(self):
        with self.assertRaises(ValueError): self.check_request(parent='d'*40)
    def test_changed_tool_in_request(self):
        with self.assertRaises(ValueError): self.check_request(changed=[guard.REQUEST, 'arbitrary.py'])
    def test_rerun_blocked(self):
        with self.assertRaises(ValueError): self.check_request(environment=dict(GITHUB_EVENT_NAME='push', GITHUB_REF=guard.BRANCH, GITHUB_RUN_ATTEMPT='2'))
    def test_dormant_request(self):
        with self.assertRaises(ValueError): self.check_request(json.loads((HERE/'REQUEST.json').read_text()))
    def test_wrong_product(self):
        d=self.request();d['candidate']='d'*40
        with self.assertRaises(ValueError): self.check_request(d)
    def test_clean_required(self):
        with self.assertRaises(ValueError): self.check_request(clean=False)
    def test_baseline_and_candidate_static(self):
        validate_logical_path(self.path(), 'baseline');validate_logical_path(self.path(True), 'candidate')
    def test_dynamic_not_assumed(self):
        for owned in (False, True): validate_logical_path(self.path(owned, True), 'candidate')
    def test_static_fallback_rejected(self):
        with self.assertRaises(ValueError): validate_logical_path(self.path(), 'candidate')
    def test_telemetry_rejected(self):
        d=self.path(True);d['logicalPathProof']['clientCallsEnabled']=True
        with self.assertRaises(ValueError): validate_logical_path(d, 'candidate')
    def test_false_backing_claim_rejected(self):
        d=self.path(True);d['logicalPathProof']['completionOwnedObserved']=False
        with self.assertRaises(ValueError): validate_logical_path(d, 'candidate')
    def test_measurement_loop_unchanged(self):
        old=(SOURCE/'tcp/r1-full-probe/Program.cs').read_text();new=(SOURCE/'tcp/logical-forwarder-probe/Program.cs').read_text()
        begin='async Task Warmup()';end='var result=new '
        self.assertEqual(old[old.index(begin):old.index(end)],new[new.index(begin):new.index(end)])
        begin='async ValueTask Operation()';end='var first='
        self.assertEqual(old[old.index(begin):old.index(end)],new[new.index(begin):new.index(end)])


if __name__ == '__main__': unittest.main()
