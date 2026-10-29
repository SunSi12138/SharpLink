using System.Text.RegularExpressions;
namespace BaselineDiagnostics;

// Only normalized public namespace/method identifiers cross this boundary, never stacks or arguments.
internal sealed record MethodLeaf(string State, string? Method = null, bool FrameworkWithSharpLinkAncestor = false);
internal sealed record MethodCount(string Method, long Samples);
internal sealed record MethodSampleSummary(bool WindowValid, MethodCount[] LeafMethods,
    long FrameworkLeafWithSharpLinkAncestorSamples, long UnavailableLeafSamples,
    long NonAllowlistedLeafSamples, long RejectedNameSamples, long OmittedAllowedSamples);
internal static class MethodSamples
{
    internal static MethodLeaf Inspect(IEnumerable<string> frames)
    {
        bool found=false, framework=false; string? method=null; string state="unavailable";
        foreach(var raw in frames)
        {
            var value=raw.Trim(); int bang=value.IndexOf('!'); if(bang>=0)value=value[(bang+1)..];
            if(value.Length==0 || value.Equals("UNKNOWN",StringComparison.OrdinalIgnoreCase) ||
               value.Equals("UNRESOLVED",StringComparison.OrdinalIgnoreCase) || value == "?" ||
               Regex.IsMatch(value,@"^(0x)?[0-9a-fA-F]+$"))continue;
            if(found)
            {
                if(framework && Normalize(value) is string ancestor && ancestor.StartsWith("SharpLink.",StringComparison.Ordinal))
                    return new(state,method,true);
                continue;
            }
            // The first resolved frame is final even if private, overlong, or not safely printable.
            found=true;
            bool allowed=value.StartsWith("System.",StringComparison.Ordinal)||value.StartsWith("Microsoft.",StringComparison.Ordinal)||value.StartsWith("SharpLink.",StringComparison.Ordinal);
            if(!allowed){state="non-allowlisted";continue;}
            method=Normalize(value);
            if(method is null){state="rejected";continue;}
            state="allowed";
            framework=method.StartsWith("System.",StringComparison.Ordinal)||method.StartsWith("Microsoft.",StringComparison.Ordinal);
        }
        return new(state,method);
    }
    private static string? Normalize(string value)
    {
        // Remove all parameter text and balanced generic substitution text, not individual values.
        int end=value.IndexOf('('); if(end>=0)value=value[..end];
        var clean=new System.Text.StringBuilder(); var closing=new Stack<char>();
        foreach(char c in value)
        {
            if(c is '[' or '<'){closing.Push(c=='['?']':'>');continue;}
            if(c is ']' or '>'){if(closing.Count==0||closing.Pop()!=c)return null;continue;}
            if(closing.Count==0)clean.Append(c);
        }
        if(closing.Count!=0)return null;
        value=clean.ToString();
        if(value.Length is <3 or >160 || !Regex.IsMatch(value,@"^[A-Za-z_][A-Za-z0-9_`]*(?:[.+][A-Za-z_][A-Za-z0-9_`]*)+$"))return null;
        if(value.Split('.').Any(part=>Regex.IsMatch(part,@"(?:^|_)0x[0-9a-fA-F]+(?:$|_)")))return null;
        return value;
    }
}
internal sealed class MethodSampleAccumulator(bool windowValid)
{
    private readonly Dictionary<string,long> counts=new(StringComparer.Ordinal);
    private long framework,unavailable,nonAllowlisted,rejected;
    internal void Add(MethodLeaf? leaf)
    {
        if(!windowValid)return;
        leaf??=new("unavailable");
        if(leaf.FrameworkWithSharpLinkAncestor)framework++;
        switch(leaf.State)
        {
            case "allowed" when leaf.Method is not null:
                if(counts.TryGetValue(leaf.Method,out var n))counts[leaf.Method]=n+1;
                else counts.Add(leaf.Method,1);
                break;
            case "non-allowlisted":nonAllowlisted++;break;
            case "rejected":rejected++;break;
            default:unavailable++;break;
        }
    }
    internal MethodSampleSummary Finish()
    {
        var top=counts.OrderByDescending(p=>p.Value).ThenBy(p=>p.Key,StringComparer.Ordinal).Take(12).Select(p=>new MethodCount(p.Key,p.Value)).ToArray();
        return new(windowValid,top,framework,unavailable,nonAllowlisted,rejected,counts.Values.Sum()-top.Sum(p=>p.Samples));
    }
}
internal static class MethodSamplesSelfTests
{
    internal static int Run()
    {
        int count=0;
        void Check(bool condition){if(!condition)throw new InvalidOperationException("method synthetic assertion");count++;}
        MethodLeaf Leaf(params string[] frames)=>MethodSamples.Inspect(frames);
        Check(Leaf("System.Core!System.Threading.Tasks.Task.Run(secretValue)").Method=="System.Threading.Tasks.Task.Run");
        Check(Leaf("System.Collections.List`1[Private.Secret].Add(Private.Secret)").Method=="System.Collections.List`1.Add");
        Check(Leaf("Microsoft.Thing.Method<Private.Secret>()").Method=="Microsoft.Thing.Method");
        Check(Leaf("UNKNOWN","0x123abc","SharpLink.Runtime.Stream.Next()").Method=="SharpLink.Runtime.Stream.Next");
        Check(Leaf("Private.Secret.Do()","SharpLink.Runtime.Stream.Next()").State=="non-allowlisted");
        Check(Leaf("Private.UnknownMethod.Do()","SharpLink.Runtime.Stream.Next()").State=="non-allowlisted");
        Check(Leaf("Private.Secret.Do()","System.Threading.Tasks.Task.Run()","SharpLink.Runtime.Stream.Next()").FrameworkWithSharpLinkAncestor==false);
        Check(Leaf("System.Threading.Tasks.Task.Run()","SharpLink.Runtime.Stream.Next()").FrameworkWithSharpLinkAncestor);
        Check(!Leaf("SharpLink.Runtime.Stream.Next()","System.Threading.Tasks.Task.Run()").FrameworkWithSharpLinkAncestor);
        Check(Leaf("System."+new string('A',161)+".Next()","SharpLink.Valid.Run()").State=="rejected");
        Check(Leaf("System.Type.0x123abc()").State=="rejected");
        Check(Leaf("System.Type.Method[Private.Secret()").State=="rejected");
        Check(Leaf("","?","UNKNOWN").State=="unavailable");
        var accumulator=new MethodSampleAccumulator(true);
        for(int i=0;i<300;i++)accumulator.Add(Leaf($"System.Type.Method{i}()"));
        for(int i=0;i<20;i++)accumulator.Add(Leaf("System.Type.Method299()"));
        accumulator.Add(Leaf("Private.Type.Method()"));accumulator.Add(Leaf("UNKNOWN"));
        accumulator.Add(Leaf("System.Type.Bad-name()"));
        var summary=accumulator.Finish();
        Check(summary.LeafMethods.Length==12 && summary.LeafMethods[0]==new MethodCount("System.Type.Method299",21));
        Check(summary.LeafMethods.Sum(x=>x.Samples)+summary.OmittedAllowedSamples+summary.NonAllowlistedLeafSamples+summary.UnavailableLeafSamples+summary.RejectedNameSamples==323);
        var disabled=new MethodSampleAccumulator(false);disabled.Add(Leaf("SharpLink.Runtime.Stream.Next()"));
        Check(!disabled.Finish().WindowValid&&disabled.Finish().LeafMethods.Length==0);
        Marker Mark(long tick)=>new(Aggregator.Source,"trace","static-intercepted",100,20,"work20s",tick);
        List<Observation> Valid()=>[new("work-start",100,42,Marker:Mark(100)),new("managed-sample",200,42,Category:"runtime-other",Leaf:Leaf("System.Type.Method()")),new("work-stop",20100,42,Marker:Mark(20100))];
        var timing=new ExpectedTiming(100,20100,1000);
        foreach(var kind in new[]{"gc","pause","contention","wait"})
        foreach(var endpoint in new[]{"start","stop"})
        foreach(var region in new[]{"before","inside","after"})
        {
            var events=Valid();double ms=region=="before"?50:region=="inside"?300:20200;
            events.Add(new(kind+"-"+endpoint,ms,42,Thread:7,Amount:7));events=events.OrderBy(e=>e.Milliseconds).ToList();
            var result=Aggregator.Analyze(events,42,0,timing);
            Check(result.Status=="Incomplete"&&result.UnmatchedEvents==1&&result.UnmatchedBreakdown.Values.Sum()==1&&result.UnmatchedBreakdown[$"{kind}:{(endpoint=="start"?"stop":"start")}:{region}"]==1);
        }
        var duplicate=Valid();duplicate.AddRange([new("wait-start",50,42),new("wait-start",300,42),new("wait-stop",400,42)]);
        var duplicated=Aggregator.Analyze(duplicate.OrderBy(e=>e.Milliseconds).ToList(),42,0,timing);
        Check(duplicated.UnmatchedEvents==1&&duplicated.UnmatchedBreakdown["wait:stop:inside"]==1&&duplicated.Status=="Incomplete");
        var absent=Valid();absent.RemoveAt(0);absent.Add(new("wait-start",300,42));
        var unavailable=Aggregator.Analyze(absent.OrderBy(e=>e.Milliseconds).ToList(),42,0,timing);
        Check(!unavailable.MethodSamples.WindowValid&&unavailable.MethodSamples.LeafMethods.Length==0&&unavailable.UnmatchedBreakdown["wait:stop:unavailable"]==1);
        var identity=Valid();identity[0]=identity[0] with{Pid=43};
        Check(!Aggregator.Analyze(identity,42,0,timing).MethodSamples.WindowValid);
        var beforeAfter=Valid();beforeAfter.Insert(0,new("managed-sample",50,42,Category:"runtime-other",Leaf:Leaf("System.Outside.Before()")));beforeAfter.Add(new("managed-sample",20200,42,Category:"runtime-other",Leaf:Leaf("System.Outside.After()")));
        Check(Aggregator.Analyze(beforeAfter,42,0,timing).MethodSamples.LeafMethods.Single()==new MethodCount("System.Type.Method",1));
        var reversed=Valid();reversed.Add(new("wait-start",50,42));reversed.Add(new("wait-start",20200,42));
        var reversedResult=Aggregator.Analyze(reversed,42,0,timing);
        Check(!reversedResult.MethodSamples.WindowValid&&reversedResult.MethodSamples.LeafMethods.Length==0&&reversedResult.UnmatchedBreakdown["wait:stop:unavailable"]==1&&reversedResult.Failures.Contains("malformed-or-reversed-event"));
        var residual=Valid();residual.AddRange([new("wait-start",50,42),new("wait-start",300,42)]);
        var residualResult=Aggregator.Analyze(residual.OrderBy(e=>e.Milliseconds).ToList(),42,0,timing);
        Check(residualResult.UnmatchedEvents==2&&residualResult.UnmatchedBreakdown["wait:stop:inside"]==1&&residualResult.UnmatchedBreakdown["wait:stop:before"]==1);
        var accounting=Valid();accounting[1]=accounting[1] with{Leaf=Leaf("System.Type.Method(secret)","SharpLink.Stream.Next()")};
        accounting.Insert(2,new("managed-sample",201,42,Category:"unknown",Leaf:Leaf("UNKNOWN")));
        accounting.Insert(3,new("external-sample",202,42));
        var accounted=Aggregator.Analyze(accounting,42,0,timing);
        Check(accounted.MethodSamples.FrameworkLeafWithSharpLinkAncestorSamples==1&&accounted.MethodSamples.UnavailableLeafSamples==1&&accounted.UnknownStacks==1&&accounted.ExternalThreadSamples==1&&accounted.Status=="Incomplete");
        var serialized=System.Text.Json.JsonSerializer.Serialize(accounted,Program.Json);
        Check(!serialized.Contains("secret")&&!serialized.Contains("SharpLink.Stream.Next")&&!serialized.Contains("System.Type.Method("));
        Check(accounted.UnmatchedBreakdown.Count==32&&accounted.UnmatchedBreakdown.Values.Sum()==accounted.UnmatchedEvents);
        return count;
    }
}
