using System.Text.Json;
namespace BaselineDiagnostics;
internal static class SelfTests
{
    static readonly ExpectedTiming Timing=new(100,20100,1000);
    static Marker Mark(long tick)=>new(Aggregator.Source,"trace","static-intercepted",100,20,"work20s",tick);
    static List<Observation> Valid()=>[
        new("work-start",100,42,Marker:Mark(100)),
        new("managed-sample",200,42,Category:"sharplink-stream"),
        new("allocation",210,42,Amount:102400),
        new("gc-start",220,42,Number:0,Amount:1),new("pause-start",221,42,Number:1),
        new("pause-stop",223,42),new("gc-stop",225,42,Amount:1),
        new("contention-start",300,42),new("wait-start",305,42),new("contention-stop",310,42),new("wait-stop",315,42),
        new("work-stop",20100,42,Marker:Mark(20100))];
    internal static int Run()
    {
        int count=0;
        void Test(Action body) { body();count++; }
        void Assert(bool ok) { if(!ok)throw new InvalidOperationException("synthetic assertion"); }
        void Invalid(List<Observation> events,string? code=null,long lost=0,bool truncated=false)
        { var result=Aggregator.Analyze(events,42,lost,Timing,truncated);Assert(result.Status=="Incomplete" && (code is null||result.Failures.Contains(code))); }
        try
        {
            Test(()=> { var r=Aggregator.Analyze(Valid(),42,0,Timing);Assert(r.Status=="Complete"&&r.AllocationTickSamples==1&&r.AllocationTickReportedBytes==102400&&r.GcStartsByGeneration["gen0"]==1&&r.GcPauses==new Interval(1,2)); });
            Test(()=> {var r=Aggregator.Analyze(Valid(),42,0,Timing);Assert(r.Contention==new Interval(1,10)&&r.WaitHandle==new Interval(1,10));var json=JsonSerializer.Serialize(r,Program.Json);Assert(!json.Contains("cpuPercent")&&!json.Contains("combinedDuration")&&json.Contains("never add"));});
            Test(()=>Invalid(Valid().Where(e=>e.Kind!="work-start").ToList(),"marker-cardinality"));
            Test(()=>Invalid(Valid().Where(e=>e.Kind!="work-stop").ToList(),"marker-cardinality"));
            Test(()=> {var v=Valid();v.Insert(1,v[0]);Invalid(v,"marker-cardinality");});
            Test(()=> {var v=Valid();v.Add(v[^1]);Invalid(v,"marker-cardinality");});
            Test(()=> {var v=Valid();(v[0],v[^1])=(v[^1],v[0]);Invalid(v,"malformed-or-reversed-event");});
            Test(()=> {var v=Valid();v[0]=v[0] with{Pid=43};Invalid(v,"marker-identity");});
            Test(()=> {var v=Valid();v[^1]=v[^1] with{Pid=43};Invalid(v,"marker-identity");});
            foreach(var marker in new[]{Mark(100) with{Source="wrong"},Mark(100) with{Variant="control"},Mark(100) with{Mode="dynamic-intercepted"},Mark(100) with{Items=1},Mark(100) with{Seconds=19},Mark(100) with{Window="warmup"},Mark(-1)})
                Test(()=> {var v=Valid();v[0]=v[0] with{Marker=marker};Invalid(v,"marker-identity");});
            Test(()=> {var v=Valid();v[^1]=v[^1] with{Milliseconds=20099};Invalid(v,"window-boundary");});
            Test(()=> {var v=Valid();v[^1]=v[^1] with{Milliseconds=25101};Invalid(v,"window-boundary");});
            Test(()=> {var v=Valid();v[^1]=v[^1] with{Marker=Mark(50)};Invalid(v,"window-boundary");});
            Test(()=>Invalid(Valid(),"lost-or-truncated",1));
            Test(()=>Invalid(Valid(),"lost-or-truncated",0,true));
            Test(()=> {var v=Valid();v.Insert(0,new("managed-sample",50,42,Category:"runtime-wait"));v.Add(new("allocation",20200,42,Amount:999));var r=Aggregator.Analyze(v,42,0,Timing);Assert(r.Status=="Complete"&&r.OutsideWindowEvents==2&&r.ManagedThreadTimeSamples["runtime-wait"]==0&&r.AllocationTickSamples==1);});
            Test(()=> {var v=Valid();v.Insert(2,new("managed-sample",205,99,Category:"runtime-wait"));var r=Aggregator.Analyze(v,42,0,Timing);Assert(r.Status=="Complete"&&r.OtherProcessEvents==1&&r.ManagedThreadTimeSamples["runtime-wait"]==0);});
            Test(()=> {var v=Valid();v[1]=v[1] with{Category="unknown"};var r=Aggregator.Analyze(v,42,0,Timing);Assert(r.UnknownStacks==1&&r.Coverage=="Partial"&&r.Status=="Incomplete"&&r.Failures.Contains("unknown-coverage"));});
            Test(()=> {var v=Valid();v.Insert(2,new("new-unsupported",205,42));var r=Aggregator.Analyze(v,42,0,Timing);Assert(r.UnknownEvents==1&&r.Coverage=="Partial"&&r.Status=="Incomplete"&&r.Failures.Contains("unknown-coverage"));});
            Test(()=> {var v=Valid();v.Insert(2,new("malformed",205,42));Invalid(v,"malformed-event");});
            Test(()=> {var v=Valid();v[1]=v[1] with{Milliseconds=double.NaN};Invalid(v,"malformed-or-reversed-event");});
            Test(()=> {var v=Valid();v[1]=v[1] with{Thread=0};Invalid(v,"malformed-event");});
            Test(()=> {var v=Valid();v[1]=v[1] with{Category="SECRET_STACK_ADDRESS"};Invalid(v,"malformed-category");var json=JsonSerializer.Serialize(Aggregator.Analyze(v,42,0,Timing),Program.Json);Assert(!json.Contains("SECRET"));});
            foreach(var kind in new[]{"contention-start","contention-stop","wait-start","wait-stop","pause-start","pause-stop","gc-start","gc-stop"})
                Test(()=>Invalid(Valid().Where(e=>e.Kind!=kind).ToList(),"unmatched-events"));
            Test(()=> {var v=Valid();v.Insert(8,new("contention-start",301,42));Invalid(v,"unmatched-events");});
            Test(()=> {var v=Valid();v.Insert(0,new("wait-start",50,42,Thread:2));v.Insert(2,new("wait-stop",150,42,Thread:2));var r=Aggregator.Analyze(v,42,0,Timing);Assert(r.Status=="Complete"&&r.CensoredIntervals["wait"]==1&&r.WaitHandle==new Interval(1,10));});
            Test(()=> {var v=Valid();v.Insert(v.Count-1,new("contention-start",20000,42,Thread:2));v.Add(new("contention-stop",20200,42,Thread:2));var r=Aggregator.Analyze(v,42,0,Timing);Assert(r.Status=="Complete"&&r.CensoredIntervals["contention"]==1&&r.Contention==new Interval(1,10));});
            Test(()=> {var v=Valid();v.Insert(0,new("wait-start",50,42,Thread:2));Invalid(v,"unmatched-events");});
            Test(()=> {var v=Valid();v.Add(new("wait-stop",20200,42,Thread:2));Invalid(v,"unmatched-events");});
            Test(()=> {Assert(Aggregator.Classify(["unknown","module!0x123abc","?"])=="unknown");Assert(Aggregator.Classify(["System.Private.CoreLib!System.Threading.Tasks.Task.Run()"] )=="task-scheduling");});
            Test(()=>Invalid(Valid().Where(e=>e.Kind!="managed-sample").ToList(),"missing-samples"));
            Test(()=> {var v=Valid();v[2]=v[2] with{Amount=-1};Invalid(v,"malformed-allocation");});
            Test(()=> {Assert(Aggregator.Classify([])=="unknown");Assert(Aggregator.Classify(["","SharpLink.Runtime.Stream.Next()"] )=="sharplink-stream");Assert(Aggregator.Classify(["System.Threading.Tasks.Task.Run()","SharpLink.Stream.Next()"] )=="task-scheduling");});
            foreach(var timing in new[]{Timing with{StartTick=99},Timing with{StopTick=20101},Timing with{Frequency=2000},Timing with{Frequency=0},Timing with{Frequency=-1},Timing with{StartTick=0},Timing with{StopTick=100},Timing with{StopTick=99}})
                Test(()=> {var r=Aggregator.Analyze(Valid(),42,0,timing);Assert(r.Status=="Incomplete"&&r.Failures.Contains("marker-measurement"));});
            Test(()=> {var v=Valid();v[0]=v[0] with{Marker=Mark(99)};Invalid(v,"marker-measurement");});
            Test(()=> {var v=Valid();v[^1]=v[^1] with{Marker=Mark(20101)};Invalid(v,"marker-measurement");});
            count+=MetadataSelfTests.Run();
            Console.WriteLine($"ANALYZER_SELF_TEST_PASS tests={count}");return 0;
        }
        catch(Exception) {Console.WriteLine($"ANALYZER_SELF_TEST_FAIL completed={count}");return 1;}
    }
}
