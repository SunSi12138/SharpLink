using SharpLink.Abstractions;
using SharpLink.Server;
using SharpLink.PackageSmoke;

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
        var failures = new SmokeFailureCollector();
        ISharpLinkServer? server = null;
        Task? runTask = null;
        Task? acceptedCancellation = null;
        try
        {
            server = SharpLinkServerBuilder.Create()
                .UseSharedMemory(args[0])
                .Build();
            runTask = server.RunUntilStoppedAsync(timeout.Token).AsTask();
            Console.WriteLine("PACKAGE_REFERENCE_ROOTING_SERVER_READY");
            try
            {
                await runTask;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                acceptedCancellation = runTask;
            }
            catch (Exception error)
            {
                // This is the actual run task, not an await wrapper's first exception.
                if (runTask.Exception is { } aggregate)
                    foreach (var failure in aggregate.InnerExceptions)
                        failures.Capture(failure);
                else
                    failures.Capture(error);
            }
        }
        catch (Exception error) { failures.Capture(error); }
        finally
        {
            if (server is not null)
            {
                await failures.AttemptAsync(() => server.StopAsync(TimeSpan.Zero, CancellationToken.None));
                if (runTask is not null)
                    await failures.ObserveUnlessAcceptedAsync(runTask, acceptedCancellation);
                await failures.AttemptAsync(server.DisposeAsync);
            }
        }
        failures.ThrowIfAny();
        return 0;
    }
}
