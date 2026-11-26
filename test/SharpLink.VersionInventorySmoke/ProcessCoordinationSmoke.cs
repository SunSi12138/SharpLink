using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.Server;

internal static partial class CoordinationSmoke
{
    internal static async Task RunProcessAsync(string role, string plugins, string handoff)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var budget = TimeSpan.FromSeconds(5);
        ReadOnlyMemory<byte> credential = "packaged-process-smoke"u8.ToArray();
        using var contractLoader = new PluginContext("process-contract", plugins);
        var contract = contractLoader.LoadFromAssemblyPath(Path.Combine(plugins, "SharpLink.DynamicPlugin.Contracts.dll"));
        var contractType = contract.GetType("SharpLink.DynamicPlugin.IDynamicPluginService", throwOnError: true)!;
        if (role == "server")
        {
            using var newLoader = new PluginContext("process-r2", plugins, contract);
            await using var services = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
            var builder = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback).UseServiceProvider(services)
                .UseAuthenticator(SharpLinkAuthenticator.CreateServer((request, _) => ValueTask.FromResult(
                    request.Payload.Span.SequenceEqual(credential.Span)
                        ? SharpLinkAuthenticationResult.Authenticate(new SharpLinkAuthenticationContext("packaged-process"))
                        : SharpLinkAuthenticationResult.Reject())))
                .RequireAuthentication()
                .ReplaceService<IVersionInventoryProbe>(new VersionInventoryProbe());
            var port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
            await using var server = builder.Build();
            server.SetVersionCoordinationMode(SharpLinkVersionCoordinationMode.Automatic);
            RequireRegistration(server.RegisterAssembly(contract), "server contract");
            var retired = await ReplaceOriginalAsync(server, newLoader, port);
            for (int attempt = 0; attempt < 10 && (retired.Context.IsAlive || retired.Assembly.IsAlive); attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(20, ct);
            }
            Check(!retired.Context.IsAlive && !retired.Assembly.IsAlive && server.GetVersionPeers().Single() == retired.Peer,
                "both the retired implementation and its load context are collected with the original Session open");
            Console.WriteLine("PACKAGE_COORDINATION_PROCESS_R1_UNLOADED same-session=true");
            Signal("server-drained");
            await WaitForAsync("client-verified");
            Console.WriteLine("PACKAGE_COORDINATION_PROCESS_SERVER_PASS original-drained=true");
        }
        else
        {
            var port = int.Parse(await WaitForAsync("server-ready"), CultureInfo.InvariantCulture);
            await using var client = SharpClientBuilder.Create().UseRequestTimeout()
                .UseAuthenticator(SharpLinkAuthenticator.CreateClient(_ => ValueTask.FromResult(credential)))
                .UseTcp(IPAddress.Loopback.ToString(), port).Build();
            client.SetVersionCoordinationMode(SharpLinkVersionCoordinationMode.Automatic);
            RequireRegistration(client.RegisterAssembly(contract), "client contract");
            await client.ConnectAsync(ct);
            var factory = typeof(CoordinationSmoke).GetMethod(nameof(PreparePlugin), BindingFlags.Static | BindingFlags.NonPublic)!;
            var plugin = (PluginProbe)factory.MakeGenericMethod(contractType).Invoke(null, [client, null])!;
            var sibling = client.CreateProxy<IVersionInventoryProbe>();
            await plugin.Prepare(budget, ct);
            await sibling.PrepareAsync(budget, ct);
            Check(await plugin.Unary(4, ct) == 5 && await sibling.Service.IncrementAsync(4, ct) == 5,
                "ordinary R1 and sibling RPCs reach the external process");

            var before = await client.QueryPeerVersionInventoryAsync(ct);
            var peer = before.Session ?? throw new InvalidOperationException("Expected the actual connected peer.");
            var participant = new SharpLinkVersionParticipant(peer, SharpLinkVersionInitiator.Server);
            var admitted = plugin.Block(ct).AsTask();
            var joining = client.JoinVersionChangeAsync(plugin.Local, participant, cancellationToken: ct).AsTask();
            Signal("client-joined");
            var resumed = await joining.WaitAsync(ct);
            var after = await client.QueryPeerVersionInventoryAsync(ct);
            Check(resumed.Resumed && resumed.Change.Session == peer && after.Session == peer &&
                  before.Registrations.Contains(resumed.Change.Current) &&
                  after.Registrations.Contains(resumed.Change.Candidate) && !admitted.IsCompleted &&
                  await plugin.Unary(4, ct) == 14 && await sibling.Service.IncrementAsync(4, ct) == 5,
                "the existing generated proxy sees the R2 binary's new behavior without reconnecting");
            Signal("client-resumed");
            Check(await admitted.WaitAsync(ct) == 42, "already-admitted work completed in the R1 binary");
            await WaitForAsync("server-drained");
            Check(await plugin.Unary(6, ct) == 16 && await sibling.Service.IncrementAsync(6, ct) == 7,
                "new and unrelated calls still work after the old owner releases");
            Signal("client-verified");
            Console.WriteLine("PACKAGE_COORDINATION_PROCESS_CLIENT_PASS same-session=true different-implementation=true");
        }

        // Keep every strong R1 reference in this activation scope. The live server,
        // shared contract and R2 loader remain owned by the caller during collection.
        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task<(WeakReference Context, WeakReference Assembly, SharpLinkVersionSession Peer)> ReplaceOriginalAsync(
            ISharpLinkServer server, PluginContext newLoader, int port)
        {
            using var oldLoader = new PluginContext("process-r1", plugins, contract);
            const string filename = "SharpLink.DynamicPlugin.Services.dll";
            var original = oldLoader.LoadFromAssemblyPath(Path.Combine(plugins, "r1", filename));
            var oldHandler = original.GetType("SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!;
            RequireRegistration(server.RegisterAssembly(original), "server implementation R1");
            await server.StartAsync(ct);
            Signal("server-ready", port.ToString(CultureInfo.InvariantCulture));
            try
            {
                await WaitForAsync("client-joined");
                await ((Task)oldHandler.GetProperty("BlockStarted")!.GetValue(null)!).WaitAsync(ct);
                // The replacement binary is deployed independently of the running R1 request.
                var replacement = newLoader.LoadFromAssemblyPath(Path.Combine(plugins, "r2", filename));
                var newHandler = replacement.GetType(oldHandler.FullName!, throwOnError: true)!;
                var declaration = SharpLinkGeneratedAssemblyCatalog.CreateSnapshot().Single(entry =>
                    entry.Contracts.Any(item => item.ContractType == contractType));
                var contractId = declaration.Contracts.Single(item => item.ContractType == contractType).ContractId;
                var before = server.GetLocalVersionInventory().Registrations.Single(item => item.ContractId == contractId);
                var peer = server.GetVersionPeers().Single();
                var participant = new SharpLinkVersionParticipant(peer, SharpLinkVersionInitiator.Server);
                var replacing = server.ReplaceAssemblyCoordinatedAsync(original, replacement, contractId,
                    [participant], budget, cancellationToken: ct).AsTask();
                await WaitForAsync("client-resumed");
                Check(!replacing.IsCompleted && Count(oldHandler, "Disposed") == 0 && Count(newHandler, "Created") == 1,
                    "the independently deployed R2 serves requests while the admitted R1 remains owned");
                oldHandler.GetMethod("ReleaseBlock")!.Invoke(null, null);
                var completed = await replacing.WaitAsync(ct);
                var after = server.GetLocalVersionInventory().Registrations.Single(item => item.ContractId == contractId);
                Check(completed.Replacement.Succeeded && completed.Replacement.ReferencesReleased &&
                      completed.Replacement.RemainingCalls == 0 && Count(oldHandler, "Disposed") == 1 &&
                      completed.Peers.Single().Session == peer && completed.Peers.Single().ActivationSent &&
                      after.RegistrationId != before.RegistrationId && after.AssemblyHash == before.AssemblyHash &&
                      server.GetVersionPeers().Single() == peer,
                    "replacement publishes a new registration and joins the old physical work on the original connection");
                return (new WeakReference(oldLoader), new WeakReference(original), peer);
            }
            finally { oldHandler.GetMethod("ReleaseBlock")!.Invoke(null, null); }
        }

        // These files sequence the two test applications only. The actual proposal,
        // approval, version facts and generated RPC still cross the live connection.
        void Signal(string marker, string value = "")
        {
            var path = Path.Combine(handoff, marker);
            File.WriteAllText(path + ".tmp", value);
            File.Move(path + ".tmp", path);
        }

        async Task<string> WaitForAsync(string marker)
        {
            var path = Path.Combine(handoff, marker);
            while (!File.Exists(path))
                await Task.Delay(15, ct);
            return await File.ReadAllTextAsync(path, ct);
        }
    }
}
