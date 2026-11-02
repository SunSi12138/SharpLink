using SharpLink.Client;
using SharpLink.Abstractions;
using SharpLink.PackageSmoke;
using SharpLink.ReferenceRooting.PackageContracts;

namespace SharpLink.ReferenceRooting.PackageClient;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 1)
            throw new ArgumentException("Expected one shared-memory name.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var failures = new SmokeFailureCollector();
        ISharpLinkClient? client = null;
        try
        {
            client = SharpClientBuilder.Create().DisableRequestTimeout()
                .UseSharedMemory(args[0])
                .Build();
            await client.ConnectAsync(timeout.Token);
            var rpc = client.CreateProxy<IReferencedPackageService>();
            await rpc.PrepareAsync(TimeSpan.FromSeconds(10), timeout.Token);
            var result = await rpc.Service.IdentifyAsync(41);
            if (!string.Equals(result, "package-service:42", StringComparison.Ordinal))
                throw new InvalidOperationException($"Unexpected referenced package service result '{result}'.");

            Console.WriteLine("PACKAGE_REFERENCE_ROOTING_PASS");
        }
        catch (Exception error) { failures.Capture(error); }
        finally
        {
            if (client is not null)
                await failures.AttemptAsync(client.DisposeAsync);
        }
        failures.ThrowIfAny();
        return 0;
    }
}
