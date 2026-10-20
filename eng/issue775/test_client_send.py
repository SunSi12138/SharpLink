import copy
import unittest
import client_send_report as r


def fixture(arm="A", concurrency=1, pool=1):
    samples, rounds = [], []
    for i in range(r.BATCHES):
        start = i*10000000+100
        rounds.append(dict(Round=i, Start=start, SendEnd=start+4000000, DrainEnd=start+8000000, QueuedBytes=0))
        for j in range(concurrency):
            samples.append(dict(Round=i, Worker=j, Thread=j+100, Completed=r.BATCH//concurrency,
                Start=start+1000, End=start+1000000, CpuStart=i*1000000, CpuEnd=i*1000000+100000,
                AsyncCalls=0, QueuedBytes=10))
    cpu = 100000*r.BATCHES*concurrency
    return dict(Schema=1, Passed=True, DiagnosticOnly=True, SourceSha=r.COMMITS[arm], Magic=137 if arm=="A" else 183,
        RoutePrefixBytes=16, ProcessorCount=2, Transport="tcp", Concurrency=concurrency, Pool=pool,
        BatchSize=r.BATCH, Batches=r.BATCHES, WarmupBatches=r.WARMUP, Operations=r.BATCH*r.BATCHES,
        Warmup=r.BATCH*r.WARMUP, ReceiverCompleted=r.BATCH*(r.BATCHES+r.WARMUP),
        UniqueReceived=r.BATCH*(r.BATCHES+r.WARMUP), BindBefore=pool if arm=="B" else 0,
        BindAfter=pool if arm=="B" else 0, FinalReservations=0, StopwatchFrequency=1000000000, ControlBytes=80,
        Measurement=dict(Start=0, End=r.BATCHES*10000000, ProcessCpuStart=0, ProcessCpuEnd=cpu+1000000,
            SenderCpuNanoseconds=cpu, OtherProcessCpuNanoseconds=1000000, AllocatedStart=0, AllocatedEnd=500000,
            Samples=samples, Rounds=rounds),
        Micro=[dict(Name=name, Operations=1000000, Start=0, End=1000000, CpuStart=0, CpuEnd=100000)
            for name in ("control", "route-admission-release")])


class SenderReaderTests(unittest.TestCase):
    def test_jit_only_has_no_timing_slots(self):
        import client_send
        self.assertEqual(client_send.measurement_slots(True), ())

    def test_timing_plan_is_unchanged(self):
        import client_send
        self.assertEqual(client_send.measurement_slots(False), tuple(enumerate(r.ORDER, 1)))

    def test_valid_arms_and_loads(self):
        for arm in "AB":
            for c,p in ((1,1),(8,4)):
                r.validate(fixture(arm,c,p),arm,f"tcp-c{c}-p{p}")

    def reject(self, mutation):
        value=fixture()
        mutation(value)
        with self.assertRaises((ValueError, KeyError)):
            r.validate(value,"A","tcp-c1-p1")

    def test_cpu_sum(self):
        self.reject(lambda v:v["Measurement"].update(SenderCpuNanoseconds=1))
    def test_other_cpu(self):
        self.reject(lambda v:v["Measurement"].update(OtherProcessCpuNanoseconds=0))
    def test_thread_migration(self):
        self.reject(lambda v:v["Measurement"]["Samples"][1].update(Thread=999))
    def test_missing_sample(self):
        self.reject(lambda v:v["Measurement"]["Samples"].pop())
    def test_missing_receive(self):
        self.reject(lambda v:v.update(UniqueReceived=1))
    def test_outside_batch(self):
        self.reject(lambda v:v["Measurement"]["Samples"][0].update(End=99999999999))
    def test_clock_reversal(self):
        self.reject(lambda v:v["Measurement"]["Samples"][0].update(CpuEnd=-1))
    def test_wrong_dialect(self):
        self.reject(lambda v:v.update(Magic=183))
    def test_rebind(self):
        self.reject(lambda v:v.update(BindAfter=1))
    def test_reservation_leak(self):
        self.reject(lambda v:v.update(FinalReservations=1))
    def test_changed_batches(self):
        self.reject(lambda v:v.update(BatchSize=8192))
    def test_async_count(self):
        self.reject(lambda v:v["Measurement"]["Samples"][0].update(AsyncCalls=9999999))
    def test_wrong_source(self):
        self.reject(lambda v:v.update(SourceSha="0"*40))
    def test_queue(self):
        self.reject(lambda v:v["Measurement"]["Rounds"][0].update(QueuedBytes=-1))
    def test_batch_reorder(self):
        self.reject(lambda v:v["Measurement"]["Rounds"].reverse())
    def test_expected_per_call_cpu(self):
        result=r.validate(fixture(),"A","tcp-c1-p1")
        self.assertEqual(result["senderCpuNsPerCall"],100000/r.BATCH)
        self.assertEqual(result["sampledMaxQueueBytes"],10)


if __name__=="__main__":
    unittest.main()
