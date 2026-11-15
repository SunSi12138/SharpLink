using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;

namespace SharpLink.N1.StaticBindEvidence;

internal static partial class Program
{
    private static async Task PublicationComponentsAsync()
    {
        // No Connect/transport activation, collectible load context or RPC. A normal codec-only
        // module is loaded after client construction so RegisterAssembly performs a real publication.
        var client = SharpClientBuilder.Create().DisableRequestTimeout().UseTcp("127.0.0.1", 1).Build();
        Exception? primary = null;
        try
        {
            var oldLocal = client.GetLocalContract<IProbe>();
            var oldChannel = client.CreateContractChannel(oldLocal);
            var oldProxy = client.CreateProxy<IProbe>();
            var originalSites = ((SharpLinkClient)client).CaptureContractBindingDiagnostics().DemandSites;
            var assembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "fixtures", "SharpLink.N1.UnrelatedCodec.dll"));
            var registration = client.RegisterAssembly(assembly);
            Check(registration.Succeeded, "unrelated-codec-real-publication");
            _ = client.CreateContractChannel(oldLocal);
            _ = client.CreateContractChannel(client.GetLocalContract<IProbe>());
            Check(((SharpLinkClient)client).CaptureContractBindingDiagnostics().DemandSites == originalSites,
                "unrelated-publication-preserves-static-generation-and-canonical-site");
            var serializes = ProbePayloadCodec.Serializes;
            var call = await ThrowsAsync<SharpLinkException>(() => oldProxy.Service.EchoAsync(new() { Value = 1 }, default).AsTask(),
                "old-static-proxy-still-valid-after-unrelated-publication").ConfigureAwait(false);
            Check(call.DetailCode == SharpLinkErrorDetails.ContractBinding.NoBoundRoute && ProbePayloadCodec.Serializes == serializes,
                "old-static-proxy-retains-no-route-not-local-retirement");
            var prepare = await ThrowsAsync<SharpLinkException>(() => oldChannel.PrepareAsync(TimeSpan.FromMilliseconds(25)).AsTask(),
                "old-static-channel-prepare-retains-original-local").ConfigureAwait(false);
            Check(prepare.Code == SharpLinkErrorCode.DeadlineExceeded, "unrelated-publication-does-not-turn-prepare-into-60002");
            GC.KeepAlive(oldProxy); GC.KeepAlive(oldChannel);
        }
        catch (Exception exception) { primary = exception; }
        try { await client.StopAsync().ConfigureAwait(false); }
        catch (Exception cleanup) { primary = primary is null ? cleanup : new AggregateException(primary, cleanup); }
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        var coordinator = SharpLinkMultiClusterClientBuilder.Create().DisableRequestTimeout().AddCluster("n1", builder => builder.UseTcp("127.0.0.1", 1).UseProtocol(options => options.MaxMetadataBytes = 16)).Build();
        Exception? coordinatorPrimary = null;
        try
        {
            var largeMetadata = new SharpLink.Sdk.SharpLinkMetadata(new System.Collections.Generic.KeyValuePair<string, string>("key", new string('x', 32)));
            _ = Throws<ArgumentOutOfRangeException>(() => coordinator.CreateProxy<IProbe>(new() { Metadata = largeMetadata }), "coordinator-metadata-uses-captured-child-limit");
            await coordinator.StopAsync().ConfigureAwait(false);
            _ = Throws<ArgumentOutOfRangeException>(() => coordinator.CreateProxy<IProbe>(new() { Metadata = largeMetadata }), "coordinator-terminal-metadata-retains-weak-scalar-child-limit");
            _ = Throws<ArgumentOutOfRangeException>(() => coordinator.CreateProxy<IProbe>(new() { Requirement = (RpcRegistrationRequirement)99 }), "coordinator-structure-before-terminal-enum");
            _ = Throws<ArgumentException>(() => coordinator.CreateProxy<IProbe>(new() { Requirement = RpcRegistrationRequirement.ExactRegistration }), "coordinator-structure-before-terminal-receipt");
            _ = Throws<InvalidOperationException>(() => coordinator.CreateProxy<IProbe>(), "coordinator-valid-options-retain-terminal-guard");
        }
        catch (Exception exception) { coordinatorPrimary = exception; }
        finally
        {
            try { await coordinator.StopAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { coordinatorPrimary = coordinatorPrimary is null ? cleanup : new AggregateException(coordinatorPrimary, cleanup); }
        }
        if (coordinatorPrimary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(coordinatorPrimary).Throw();
    }
}
