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
        bool markerCheck=args.Length>0&&args[0]=="--marker-check";
        try
        {
            if(args.Length!=12 || args[0] is not ("--trace" or "--marker-check") || args[2]!="--output" || args[4]!="--expected-pid" ||
               args[6]!="--start-tick" || args[8]!="--stop-tick" || args[10]!="--frequency")return 64;
            output=Path.GetFullPath(args[3]);
            var trace=Path.GetFullPath(args[1]);
            if(File.Exists(output)||File.Exists(output+".etlx"))return 64;
            var pid=int.Parse(args[5],NumberStyles.None,CultureInfo.InvariantCulture);
            if(pid<=0 || !long.TryParse(args[7],NumberStyles.None,CultureInfo.InvariantCulture,out var startTick) ||
                !long.TryParse(args[9],NumberStyles.None,CultureInfo.InvariantCulture,out var stopTick) ||
                !long.TryParse(args[11],NumberStyles.None,CultureInfo.InvariantCulture,out var frequency))return 64;
            var expectedTiming=new ExpectedTiming(startTick,stopTick,frequency);
            if(!(markerCheck?expectedTiming.IsValidFor(500,5000):expectedTiming.IsValid))return 64;
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            object summary=markerCheck
                ?MarkerCheck.Analyze(TraceAdapter.ReadEvents(trace,output+".etlx"),pid,expectedTiming)
                :TraceAdapter.Read(trace,output+".etlx",pid,expectedTiming);
            string status=summary is MarkerSummary smoke?smoke.Status:((DiagnosticSummary)summary).Status;
            File.WriteAllText(output,JsonSerializer.Serialize(summary,Json));
            // No trace payloads, stack names, addresses, filenames or exceptions on stdout/stderr.
            Console.WriteLine(status=="Complete"?"ANALYZER_COMPLETE":"ANALYZER_INCOMPLETE");
            return status=="Complete"?0:2;
        }
        catch(Exception)
        {
            if(output is not null && !File.Exists(output))
            {
                try {
                    object failure=markerCheck
                        ?new MarkerSummary(1,"synthetic-marker-check","Incomplete",["parse-failure"],new MarkerCensus(),0,0,0,false,false)
                        :Aggregator.Analyze([],1,0,new ExpectedTiming(1,20001,1000)) with { Failures=["parse-failure"] };
                    File.WriteAllText(output,JsonSerializer.Serialize(failure,Json));
                }
                catch(Exception) { /* No exception data leaves this local parser. */ }
            }
            Console.WriteLine("ANALYZER_INCOMPLETE"); return 2;
        }
    }
}
