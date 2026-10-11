using System;
using System.Buffers;
using System.Reflection;
using System.Threading.Tasks;

namespace SharpLink.UnitTests.Runtime;

// These run against the shipping (non-resolved, non-B3) request router.
// The private field is inspected only to prove the allocation boundary;
// all routing and cleanup calls below use StreamManager's real API.
public partial class StreamManagerTests
{
    [Test]
    public async Task DefaultOnlyRouteMustNeverMaterializeNamedDictionary()
    {
        var manager = new StreamManager();
        var dispatcher = new RecordingDispatcher();
        manager.Register(7001, 0, dispatcher);
        var routes = NamedDictionaryRoutes(manager, 7001);
        NamedDictionaryEnsure(!NamedDictionaryExists(routes),
            "registering stream 0 unexpectedly allocated named-route storage");
        await manager.DispatchChunkAsync(7001, 0, new ReadOnlySequence<byte>(new byte[] { 1 }));
        NamedDictionaryEnsure(dispatcher.DispatchCount == 1, "default stream failed to dispatch");
        manager.Unregister(7001, 0);
        NamedDictionaryEnsure(!NamedDictionaryExists(routes),
            "removing a default stream materialized named-route storage");
        manager.AssertAccountingInvariant();
    }

    [Test]
    public async Task MissingNamedRouteOperationsMustNotMaterializeTheDictionary()
    {
        var manager = new StreamManager();
        manager.Register(7002, 0, new RecordingDispatcher());
        var routes = NamedDictionaryRoutes(manager, 7002);
        await manager.DispatchChunkAsync(7002, 7, new ReadOnlySequence<byte>(new byte[] { 2 }));
        manager.Unregister(7002, 7);
        manager.CompleteStream(7002, 7, exception: null);
        manager.CompletePeerStream(7002, 7, exception: null);
        NamedDictionaryEnsure(!NamedDictionaryExists(routes),
            "a missing named route allocated a dictionary");
        manager.CompleteRequestStreams(7002, null);
        NamedDictionaryEnsure(!NamedDictionaryExists(routes),
            "default-only request cleanup allocated named-route storage");
        manager.AssertAccountingInvariant();
    }

    [Test]
    public async Task FirstNamedRouteMustMaterializeDictionaryAndKeepNamedIdentity()
    {
        var manager = new StreamManager();
        var defaultStream = new RecordingDispatcher();
        var named = new RecordingDispatcher();
        manager.Register(7003, 0, defaultStream);
        var routes = NamedDictionaryRoutes(manager, 7003);
        NamedDictionaryEnsure(!NamedDictionaryExists(routes), "pre-named registry was not lazy");
        manager.Register(7003, 7, named);
        NamedDictionaryEnsure(NamedDictionaryExists(routes), "nonzero route did not create its map");
        await manager.DispatchChunkAsync(7003, 7, new ReadOnlySequence<byte>(new byte[] { 7 }));
        await manager.DispatchChunkAsync(7003, 0, new ReadOnlySequence<byte>(new byte[] { 0 }));
        NamedDictionaryEnsure(named.DispatchCount == 1 && defaultStream.DispatchCount == 1,
            "mixed default/named route identities changed");
        manager.Unregister(7003, 7);
        manager.Unregister(7003, 0);
        manager.AssertAccountingInvariant();
    }

    [Test]
    public void DefaultOnlyCompleteAllMustNotAllocateNamedDictionary()
    {
        var manager = new StreamManager();
        var first = new RecordingDispatcher();
        var second = new RecordingDispatcher();
        manager.Register(7004, 0, first);
        manager.Register(7005, 0, second);
        var firstRoutes = NamedDictionaryRoutes(manager, 7004);
        var secondRoutes = NamedDictionaryRoutes(manager, 7005);
        manager.CompleteAll(new OperationCanceledException("connection terminated"));
        NamedDictionaryEnsure(!NamedDictionaryExists(firstRoutes) && !NamedDictionaryExists(secondRoutes),
            "default-only CompleteAll created a named-route map");
        NamedDictionaryEnsure(first.CompleteCount == 1 && second.CompleteCount == 1,
            "default-only CompleteAll did not complete both streams");
        manager.AssertAccountingInvariant();
    }

    [Test]
    public async Task NamedRouteCleanupMustLeaveDefaultRouteLive()
    {
        var manager = new StreamManager();
        var defaultStream = new RecordingDispatcher();
        var named = new RecordingDispatcher();
        manager.Register(7006, 0, defaultStream);
        manager.Register(7006, 8, named);
        manager.CompleteStream(7006, 8, new OperationCanceledException("named route closed"));
        await manager.DispatchChunkAsync(7006, 0, new ReadOnlySequence<byte>(new byte[] { 1 }));
        NamedDictionaryEnsure(defaultStream.DispatchCount == 1 &&
            named.CompleteCount == 1 && named.DispatchCount == 0,
            "named cleanup damaged the default route");
        manager.Unregister(7006, 0);
        manager.AssertAccountingInvariant();
    }

    [Test]
    public async Task ConcurrentDefaultAndNamedRegistrationMustPreserveBothRoutes()
    {
        for (var round = 0; round < 64; round++)
        {
            var manager = new StreamManager();
            var defaultStream = new RecordingDispatcher();
            var named = new RecordingDispatcher();
            await Task.WhenAll(
                Task.Run(() => manager.Register(9000 + round, 0, defaultStream)),
                Task.Run(() => manager.Register(9000 + round, 7, named)));
            NamedDictionaryEnsure(manager.ActiveStreamCount == 2 &&
                NamedDictionaryExists(NamedDictionaryRoutes(manager, 9000 + round)),
                "parallel default/named registration lost a route or the named map");
            await manager.DispatchChunkAsync(9000 + round, 0, new ReadOnlySequence<byte>(new byte[] { 0 }));
            await manager.DispatchChunkAsync(9000 + round, 7, new ReadOnlySequence<byte>(new byte[] { 7 }));
            NamedDictionaryEnsure(defaultStream.DispatchCount == 1 && named.DispatchCount == 1,
                "parallel registration changed stream identity");
            manager.CompleteAll(exception: null);
            manager.AssertAccountingInvariant();
        }
    }

    private static object NamedDictionaryRoutes(StreamManager manager, long requestId)
    {
        var field = typeof(StreamManager).GetField(
            "_dispatchersByRequestId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var map = field.GetValue(manager) ??
            throw new InvalidOperationException("The request-id route map was not initialized.");
        var get = map.GetType().GetMethod("TryGetValue",
            BindingFlags.Instance | BindingFlags.Public)!;
        object?[] args = [requestId, null];
        var found = (bool)get.Invoke(map, args)!;
        if (!found || args[1] is null)
            throw new InvalidOperationException("Cannot inspect an existing request route.");
        return args[1]!;
    }

    private static bool NamedDictionaryExists(object routes)
    {
        var field = routes.GetType().GetField(
            "_byStreamId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return field.GetValue(routes) is not null;
    }

    private static void NamedDictionaryEnsure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
