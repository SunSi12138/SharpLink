using System.Globalization;
using System.Text.Json;
namespace BaselineDiagnostics;
internal static class Program
{
    internal static readonly JsonSerializerOptions Json=new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase, WriteIndented=true };
    static int Main(string[] args)
    {
        if(args.SequenceEqual(new[]{"--self-test"}))return SelfTests.Run();
        string? output=null;
        try
        {
            if(args.Length!=12 || args[0]!="--trace" || args[2]!="--output" || args[4]!="--expected-pid" ||
               args[6]!="--start-tick" || args[8]!="--stop-tick" || args[10]!="--frequency")return 64;
            output=Path.GetFullPath(args[3]);
            var trace=Path.GetFullPath(args[1]);
            if(File.Exists(output)||File.Exists(output+".etlx"))return 64;
            var pid=int.Parse(args[5],NumberStyles.None,CultureInfo.InvariantCulture);
            if(pid<=0 || !long.TryParse(args[7],NumberStyles.None,CultureInfo.InvariantCulture,out var startTick) ||
                !long.TryParse(args[9],NumberStyles.None,CultureInfo.InvariantCulture,out var stopTick) ||
                !long.TryParse(args[11],NumberStyles.None,CultureInfo.InvariantCulture,out var frequency))return 64;
            var expectedTiming=new ExpectedTiming(startTick,stopTick,frequency);
            if(!expectedTiming.IsValid)return 64;
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var summary=TraceAdapter.Read(trace,output+".etlx",pid,expectedTiming);
            File.WriteAllText(output,JsonSerializer.Serialize(summary,Json));
            // No trace payloads, stack names, addresses, filenames or exceptions on stdout/stderr.
            Console.WriteLine(summary.Status=="Complete"?"ANALYZER_COMPLETE":"ANALYZER_INCOMPLETE");
            return summary.Status=="Complete"?0:2;
        }
        catch(Exception)
        {
            if(output is not null && !File.Exists(output))
            {
                try { File.WriteAllText(output,JsonSerializer.Serialize(Aggregator.Analyze([],1,0,new ExpectedTiming(1,20001,1000)) with { Failures=["parse-failure"] },Json)); }
                catch(Exception) { /* No exception data leaves this local parser. */ }
            }
            Console.WriteLine("ANALYZER_INCOMPLETE"); return 2;
        }
    }
}
