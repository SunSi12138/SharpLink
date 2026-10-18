#if SHARPLINK_STATIC_BINDING_PROTOTYPE
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;

namespace SharpLink.Benchmarks;

// Diagnostic overlay only. Never compiled into the uninstrumented benchmark checkout.
internal static class BoundaryDiagnostics
{
    internal static async Task RunAsync(string[] args)
    {
        if (args.Length != 3 || args[0] is not ("tcp" or "sharedmemory") ||
            args[1] is not ("unary" or "oneway"))
            throw new ArgumentException("transport shape output.json");
        var oneWay = args[1] == "oneway";
        var pool = oneWay ? 1 : 4;
        var concurrency = oneWay ? 1 : 8;
        var batches = oneWay ? 512 : 8;
        var batchSize = oneWay ? 2048 : 65536;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await using var fixture = await StaticBindingFixture.CreateAsync(args[0], pool);
        long sent = 0, received = 0;
        for (var batch = 0; batch < batches; batch++)
        {
            if (oneWay)
            {
                fixture.Receiver.Reset(batchSize);
                for (var i = 0; i < batchSize; i++)
                    await fixture.Rpc.PublishEventAsync(i, Stopwatch.GetTimestamp(), "binding");
                // A real receiver-completion condition, not a fixed delay or wire ACK.
                // No next batch can extend the original bounded send queue.
                while (fixture.Receiver.Completed < batchSize || fixture.Receiver.LastCompletion == 0)
                {
                    budget.Token.ThrowIfCancellationRequested();
                    await Task.Yield();
                }
                StaticBindingEvidence.Require(fixture.Receiver.Completed == batchSize &&
                    fixture.Receiver.Seen.All(static n => n == 1), "Incomplete or duplicate diagnostic batch");
                received += fixture.Receiver.Completed;
            }
            else
            {
                await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async worker =>
                {
                    for (var i = worker; i < batchSize; i += concurrency)
                        StaticBindingEvidence.Require(await fixture.Rpc.AddAsync(i, 7) == i + 7,
                            "Diagnostic Unary result");
                })).WaitAsync(budget.Token);
                received += batchSize;
            }
            sent += batchSize;
            // An existing Unary response is a diagnostic scheduling boundary, not a receipt.
            StaticBindingEvidence.Require(await fixture.Rpc.AddAsync(20, 22).AsTask().WaitAsync(budget.Token) == 42,
                "Diagnostic batch boundary");
        }
        var bindings = fixture.Client.StaticBindingDiagnostics<IBenchmarkRpc>();
        StaticBindingEvidence.Require(bindings == (pool, (long)pool, 0), "No diagnostic rebinds");
        await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(new
        {
            Schema = 1,
            Passed = true,
            Magic = (int)ProtocolV2Constants.Magic,
            Transport = args[0],
            Shape = args[1],
            Pool = pool,
            Concurrency = concurrency,
            Batches = batches,
            BatchSize = batchSize,
            Sent = sent,
            Received = received,
            BoundaryUnaryCalls = batches,
            BindCount = bindings.Binds,
            PendingBinds = bindings.Pending,
            ProcessorCount = Environment.ProcessorCount,
            Runtime = Environment.Version.ToString(),
            PerformanceEvidence = false
        }));
    }
}
#endif
