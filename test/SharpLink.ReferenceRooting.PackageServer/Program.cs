using SharpLink.Abstractions;
using SharpLink.Server;

namespace SharpLink.ReferenceRooting.PackageServer;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 1)
            throw new ArgumentException("Expected one shared-memory name.");

        var manifests = SharpLinkGeneratedAssemblyCatalog.CreateSnapshot()
            .Where(static manifest => string.Equals(
                manifest.OwnerAssembly.GetName().Name,
                "SharpLink.ReferenceRooting.PackageServices",
                StringComparison.Ordinal))
            .ToArray();
        if (manifests.Length != 1 ||
            manifests[0].Services.Count != 1 ||
            manifests[0].Services[0].ImplementationType.IsPublic)
        {
            throw new InvalidOperationException(
                "The referenced internal package service manifest was not rooted before server Build.");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var server = SharpLinkServerBuilder.Create()
            .UseSharedMemory(args[0])
            .Build();
        var runTask = server.RunAsync(timeout.Token).AsTask();
        Console.WriteLine("PACKAGE_REFERENCE_ROOTING_SERVER_READY");
        try
        {
            await runTask;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
        }
        return 0;
    }
}
