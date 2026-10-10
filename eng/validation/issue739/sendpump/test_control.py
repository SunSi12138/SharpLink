#!/usr/bin/env python3
"""Small rejection tests for the finite SendPump validator; optionally audit a run directory."""
import copy
import importlib.util
import json
from pathlib import Path
import sys
import unittest

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("sendpump_run", HERE/"run.py")
run = importlib.util.module_from_spec(spec);spec.loader.exec_module(run)
BINARY = {"SharpLink.Benchmarks.dll":"a"*64,"SharpLink.Runtime.dll":"b"*64}
RUNTIME = {"System.Private.CoreLib.dll":"c"*64}


def sample(kind):
    fixture,force = kind.startswith("fixture"),kind=="pump-force-sync"
    gated = kind not in ("pump-idle-sync","pump-force-sync","fixture-sync")
    fpc = 2 if kind in ("pump-capacity-completed","pump-capacity-incomplete","fixture-pair") else 1
    def counts(n):
        return {"Cycles":n,"Frames":n*fpc,"LeaseActiveObservations":n*fpc*(2 if gated else 1),
            "LeaseReturnedObservations":n*fpc,"PreParked":0 if fixture else n,"PostParked":0 if fixture else n,
            "FirstAdmissionsCompleted":n if not fixture and not force else 0,"SecondCompleted":n if kind=="pump-capacity-completed" else 0,
            "SecondIncomplete":n if kind=="pump-capacity-incomplete" else 0,"ForceCompletionConsumed":n if force else 0,
            "HeldQueueChecks":n*fpc if gated and not fixture else 0,
            "Writer":{"Flushes":n*fpc,"Frames":n*fpc,"Bytes":n*fpc*15,"SyncFlushes":n*(fpc-int(gated)),"TokenWraps":0,
                **{k:n*int(gated) for k in ("PendingFlushes","Resets","RegistrationEntries","RegistrationsAccepted","RegistrationsReturned","Releases","GetResults")}}}
    return {"schemaVersion":1,"sourceSha":run.SOURCE,"kind":kind,"sample":"test","runtime":"10.0.2","expectedRuntime":"10.0.2",
        "framework":".NET 10.0.2","corelibPath":"/shared/Microsoft.NETCore.App/10.0.2/System.Private.CoreLib.dll","corelibSha256":"c"*64,
        "executableSha256":"a"*64,"runtimeAssemblySha256":"b"*64,"cycles":128,"warmup":64,"threadStart":1,"threadEnd":1,
        "processId":1,"architecture":"X64","serverGc":False,"precise":True,"driverIncluded":True,"subtractionApplied":False,
        "fixtureOnly":fixture,"forceFlushCompletionContract":force,"controlledIncompleteFlush":gated,"framesPerCycle":fpc,
        "frames":128*fpc,"frameBytes":15,"queueCapacity":15 if kind=="pump-capacity-incomplete" else 30,
        "profile":"Balanced","explicitTimedBatch":False,"callerCancellationToken":"None","writerPendingEnd":0,
        "writerRegistrationInFlightEnd":0,"writerConsumedEnd":1,"maximumRegistrationInFlight":int(gated),
        "counts":counts(128),"warmupCounts":counts(64),"bytes":256,"bytesPerCycle":2,"bytesPerFrame":2/fpc,
        "gen0Collections":0,"elapsedSeconds":1,"nanosecondsPerCycle":1e9/128,"nanosecondsPerFrame":1e9/(128*fpc),
        "cpuSeconds":.1,"ticksStart":0,"ticksEnd":1000000000,"stopwatchFrequency":1000000000}


def validate(row):
    run.validate_sample(row,row["kind"],"test",128,64,"10.0.2","10.0.2",BINARY,RUNTIME)


class Controls(unittest.TestCase):
    def test_all_cases_valid(self):
        for kind in run.CASES:
            with self.subTest(kind=kind): validate(sample(kind))
    def test_all_lifecycle_fields_fail_closed(self):
        for kind in run.CASES:
            for group in ("counts","warmupCounts"):
                original=sample(kind)
                for key in original[group]:
                    if key=="Writer":continue
                    row=copy.deepcopy(original);row[group][key]+=1
                    with self.subTest(kind=kind,group=group,key=key),self.assertRaises(ValueError):validate(row)
                for key in original[group]["Writer"]:
                    row=copy.deepcopy(original);row[group]["Writer"][key]+=1
                    with self.subTest(kind=kind,group=group,key=key),self.assertRaises(ValueError):validate(row)
    def test_open_source_or_wrong_activation_rejected(self):
        for key,value in {"writerPendingEnd":1,"writerRegistrationInFlightEnd":1,"writerConsumedEnd":0,
                          "maximumRegistrationInFlight":2,"queueCapacity":16,"frameBytes":16,
                          "forceFlushCompletionContract":True,"controlledIncompleteFlush":False}.items():
            row=sample("pump-capacity-incomplete");row[key]=value
            with self.subTest(key=key),self.assertRaises(ValueError):validate(row)
    def test_wrong_runtime_and_binaries_rejected(self):
        for key in ("sourceSha","runtime","expectedRuntime","framework","corelibSha256","executableSha256","runtimeAssemblySha256"):
            row=sample("pump-idle-sync");row[key]="wrong"
            with self.subTest(key=key),self.assertRaises(ValueError):validate(row)
    def test_metrics_and_denominators_rejected(self):
        for key,value in {"bytes":-1,"bytesPerCycle":3,"bytesPerFrame":3,"elapsedSeconds":0,
                          "cpuSeconds":float("nan"),"gen0Collections":-1,"nanosecondsPerFrame":1,"threadEnd":2}.items():
            row=sample("fixture-pair");row[key]=value
            with self.subTest(key=key),self.assertRaises(ValueError):validate(row)
    def test_missing_counter_rejected(self):
        row=sample("pump-capacity-incomplete");del row["counts"]["Writer"]["RegistrationsReturned"]
        with self.assertRaises(KeyError):validate(row)
    def test_net11_summary_keeps_aa_separate(self):
        rows=[]
        for kind in run.CASES:
            for cycle in (1,2,3):
                for variant in "ABBA":
                    rows.append(dict(kind=kind,variant=variant,group="ABBA",cycle=cycle,bytesPerCycle=10 if variant=="A" else 5,
                                     bytesPerFrame=10,nanosecondsPerCycle=1,cpuSeconds=1,gen0Collections=0))
            for value in (100,200):
                rows.append(dict(kind=kind,variant="A",group="AA",cycle=None,bytesPerCycle=value,bytesPerFrame=value,
                                 nanosecondsPerCycle=1,cpuSeconds=1,gen0Collections=0))
        for cell in run.summarize(rows,"net11"):
            self.assertEqual(len(cell["variants"]["A"]["bytesPerCycle"]["values"]),6)
            self.assertEqual(cell["variants"]["A"]["bytesPerCycle"]["median"],10)
            self.assertEqual(cell["aaBytesPerCycle"],[100,200])
            self.assertEqual(cell["abbaCycleMedianDifferenceBMinusA"],[-5,-5,-5])
    def test_sequence_sizes(self):
        self.assertEqual(len(run.CASES)*6,48)
        self.assertEqual(len(run.CASES)*(3*4+2),112)
    def test_positive_float_counter_rejected(self):
        row=sample("fixture-pair");row["counts"]["Frames"]=256.0
        with self.assertRaises(ValueError):validate(row)


def audit(directory):
    provenance=json.loads((directory/"provenance.json").read_text())
    rows=json.loads((directory/"completed-samples.json").read_text())
    sdk,runtime,version,tfm=run.PINS[provenance["stage"]]
    assert len(rows)==provenance["expectedRecords"]
    assert len({r["processId"] for r in rows})==len(rows)
    for row in rows:
        path=directory/(row["sample"]+".sample.json");raw=json.loads(path.read_text())
        assert run.sha(path)==row["sampleSha256"]==provenance["rawSampleHashes"][path.name]
        run.validate_sample(raw,row["kind"],row["sample"],provenance["cycles"],provenance["warmup"],runtime,version,
                            provenance["binaryHashes"][row["variant"]],provenance["runtimeHashes"])
    for variant in provenance["binaryHashes"]:
        assert run.reviewed.binary_manifest(directory/"executed-binaries"/variant/"driver")==provenance["binaryHashes"][variant]
    print(f"Audited {len(rows)} raw records, unique PIDs, exact lifecycle and exported binary/config hashes.")


if __name__=="__main__":
    directory=Path(sys.argv.pop(1)) if len(sys.argv)>1 else None
    result=unittest.main(exit=False)
    if not result.result.wasSuccessful():sys.exit(1)
    if directory:audit(directory)
