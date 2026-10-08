using System.Runtime.CompilerServices;
using static SharpLink.UnitTests.Client.ClientStreamWaitTestSupport;

namespace SharpLink.UnitTests.Client;

public sealed class ClientStreamWaitRetentionTests
{
    private static readonly AsyncLocal<object?> ContextMarker = new();
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments("logical", "none")]
    [Arguments("telemetry", "none")]
    [Arguments("both", "none")]
    [Arguments("logical", "context")]
    [Arguments("telemetry", "context")]
    [Arguments("both", "context")]
    [Arguments("logical", "wrapper")]
    [Arguments("telemetry", "wrapper")]
    [Arguments("both", "wrapper")]
    public void ConsumedWrapperCacheReleasesContextClientAndEnumerator(string wrapper, string retained)
    {
        CacheProbe? probe = null;
        Exception? failure = null;
        var creator = new Thread(() =>
        {
            try { probe = CreateProbe(wrapper, retained); }
            catch (Exception error) { failure = error; }
        });
        using (ExecutionContext.SuppressFlow()) creator.Start();
        Require(creator.Join(Bound), "retention creator did not exit");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        var completed = probe ?? throw new InvalidOperationException("missing wrapper retention probe");
        Collect();
        Require(completed.Context.IsAlive == (retained == "context"),
            "context leaked from consumed wrapper, or deliberate ExecutionContext root was not detected");
        foreach (var reference in completed.ObjectReferences)
            Require(reference.IsAlive == (retained == "wrapper"),
                "client/enumerator leaked from consumed wrapper, or deliberate wrapper root was not detected");
        completed.DeliberateRoot = null;
        Collect();
        Require(!completed.Context.IsAlive, "released context root remained retained by a consumed wrapper");
        foreach (var reference in completed.ObjectReferences)
            Require(!reference.IsAlive, "consumed wrapper cache still retains its client or enumerator");
        // Keep all exact state-machine boxes alive, including the inner telemetry
        // box in nested mode. Collecting a broken cache cannot make this test pass.
        GC.KeepAlive(completed.ResultSources);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static CacheProbe CreateProbe(string wrapper, string retained)
    {
        var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        var source = new PendingStream(client);
        var boxes = new List<object>();
        var reader = Wrap(client, source, wrapper, resultSources: boxes);
        var marker = new byte[1024 * 1024];
        var context = new WeakReference(marker);
        ContextMarker.Value = marker;
        try
        {
            var read = reader.MoveNextAsync();
            Require(!read.IsCompleted, "retention must exercise a suspended wrapper");
            boxes.Add(ResultSource(read));
            Require(boxes.Count == (wrapper == "both" ? 2 : 1), "missing inner or outer result box");
            object? root = retained switch
            {
                "context" => ExecutionContext.Capture(),
                "wrapper" => reader,
                _ => null
            };
            source.Finish(false);
            Require(read.IsCompletedSuccessfully, "retention completion did not unwind inline");
            Require(!read.GetAwaiter().GetResult(), "retention terminal result changed");
            Require(source.ConsumptionCount == 1, "retention source was consumed more than once");
            return new CacheProbe(context,
                [new WeakReference(client), new WeakReference(source), new WeakReference(reader)],
                boxes.ToArray(), root);
        }
        finally
        {
            ContextMarker.Value = null;
            source.ReleaseForCleanup();
            reader.DisposeAsync().AsTask().GetAwaiter().GetResult();
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void Collect()
    {
        for (var pass = 0; pass < 3; pass++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private sealed class CacheProbe(WeakReference context, WeakReference[] references, object[] boxes, object? root)
    {
        internal WeakReference Context { get; } = context;
        internal WeakReference[] ObjectReferences { get; } = references;
        internal object[] ResultSources { get; } = boxes;
        internal object? DeliberateRoot = root;
    }
}
