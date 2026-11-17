using System.Reflection;
using SharpLink.Abstractions;

var assemblyPath = Environment.GetEnvironmentVariable("N1_CLIENT_DLL")!;
var assembly = Assembly.LoadFrom(assemblyPath);
var clientType = assembly.GetType("SharpLink.Client.SharpLinkClient", throwOnError: true)!;
var method = clientType.GetMethod("IsExpectedContractRefreshStopExit", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
var cases = new (string Name, bool Stop, Exception Exception)[]
{
    ("stop+ConnectionClosed", true, new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "probe")),
    ("stop+DeadlineExceeded", true, new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "probe")),
    ("no-stop+ConnectionClosed", false, new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "probe")),
    ("stop+InvalidOperation", true, new InvalidOperationException("probe"))
};
foreach (var c in cases)
{
    var result = (bool)method.Invoke(null, new object[] { c.Stop, c.Exception })!;
    Console.WriteLine($"{c.Name}={result}");
}
return 0;
