using SharpLink.Client;
using SharpLink.ReferenceRooting.PackageContracts;

namespace SharpLink.ReferenceRooting.PackageClient;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 1)
            throw new ArgumentException("Expected one shared-memory name.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = SharpClientBuilder.Create()
            .DisableRequestTimeout()
            .UseSharedMemory(args[0])
            .Build();
        await client.ConnectAsync(timeout.Token);
        var result = await client.Get<IReferencedPackageService>().IdentifyAsync(41);
        if (!string.Equals(result, "package-service:42", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unexpected referenced package service result '{result}'.");

        Console.WriteLine("PACKAGE_REFERENCE_ROOTING_PASS");
        return 0;
    }
}
