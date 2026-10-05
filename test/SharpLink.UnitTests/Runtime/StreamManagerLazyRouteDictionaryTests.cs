using System.Reflection;

namespace SharpLink.UnitTests.Runtime;

[NotInParallel("route-footprint")]
public sealed class StreamManagerLazyRouteDictionaryTests
{
    [Test]
    public void DefaultOnlyRouteDoesNotAllocateNamedDictionary()
    {
        var manager = new StreamManager();
        var dispatcher = new RecordingDispatcher();
        try
        {
            manager.Register(9810, 0, dispatcher);
            Require(ReadNamedDictionary(manager, 9810) is null,
                "default stream 0 must not allocate the non-default route dictionary");
        }
        finally
        {
            manager.CompleteAll(exception: null);
        }
    }

    [Test]
    public async Task AddingAndRemovingNamedRoutePreservesDefaultRoute()
    {
        var manager = new StreamManager();
        var first = new RecordingDispatcher();
        var named = new RecordingDispatcher();
        var replacement = new RecordingDispatcher();
        var payload = new ReadOnlySequence<byte>(new byte[] { 1 });
        try
        {
            manager.Register(9811, 0, first);
            manager.Register(9811, 7, named);
            Require(ReadNamedDictionary(manager, 9811) is not null,
                "a published named route must have a registry map");
            await manager.DispatchChunkAsync(9811, 0, payload);
            await manager.DispatchChunkAsync(9811, 7, payload);
            manager.CompleteStream(9811, 0, exception: null);
            Require(first.Completions == 1 && manager.ActiveStreamCount == 1,
                "default completion must preserve the named route");
            manager.Register(9811, 0, replacement);
            await manager.DispatchChunkAsync(9811, 0, payload);
            await manager.DispatchChunkAsync(9811, 7, payload);
            Require(first.Dispatches == 1 && named.Dispatches == 2 && replacement.Dispatches == 1,
                "inline and named route identities must remain independent across default replacement");
            manager.CompleteRequestStreams(9811, exception: null);
            Require(named.Completions == 1 && replacement.Completions == 1 && manager.ActiveStreamCount == 0,
                "request retirement must account for both registry forms exactly once");
        }
        finally
        {
            manager.CompleteAll(exception: null);
        }
    }

    [Test]
    public async Task MissingNamedOperationsLeaveDefaultRouteUntouched()
    {
        var manager = new StreamManager();
        var dispatcher = new RecordingDispatcher();
        var payload = new ReadOnlySequence<byte>(new byte[] { 1 });
        try
        {
            manager.Register(9812, 0, dispatcher);
            var originalDictionary = ReadNamedDictionary(manager, 9812);
            manager.Unregister(9812, 7);
            manager.CompleteStream(9812, 7, exception: null);
            manager.CompletePeerStream(9812, 7, exception: null);
            manager.AbandonExistingRequestStreams(9812, 7);
            await manager.DispatchChunkAsync(9812, 7, payload);
            Require(!manager.TryDispatchPreAdmissionCompressed(9812, 7, payload, 1, out _),
                "a missing named route must not acquire compressed DATA");
            Require(ReferenceEquals(ReadNamedDictionary(manager, 9812), originalDictionary),
                "queries and missing-route cleanup must not materialize or replace the named map");
            await manager.DispatchChunkAsync(9812, 0, payload);
            Require(dispatcher.Dispatches == 1 && dispatcher.Completions == 0 && manager.ActiveStreamCount == 1,
                "operations for absent named routes must not complete or strand the default route");
        }
        finally
        {
            manager.CompleteAll(exception: null);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void ConnectionCompletionHandlesDefaultOnlyAndMixedRoutes(bool includeNamed)
    {
        var manager = new StreamManager();
        var defaultDispatcher = new RecordingDispatcher();
        var namedDispatcher = new RecordingDispatcher();
        manager.Register(9813, 0, defaultDispatcher);
        if (includeNamed) manager.Register(9813, 9, namedDispatcher);
        manager.CompleteAll(new OperationCanceledException("connection ended"));
        manager.CompleteAll(new OperationCanceledException("duplicate connection end"));
        Require(manager.ActiveStreamCount == 0 && defaultDispatcher.Completions == 1 &&
                namedDispatcher.Completions == (includeNamed ? 1 : 0),
            "connection completion must drain both lazy and materialized route registries exactly once");
    }

    [Test]
    public void ConcurrentDefaultAndNamedRegistrationMustNotLoseRoutes()
    {
        const int count = 16;
        var manager = new StreamManager();
        var dispatchers = new RecordingDispatcher[count];
        var payload = new ReadOnlySequence<byte>(new byte[] { 1 });
        try
        {
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, index =>
            {
                var dispatcher = new RecordingDispatcher();
                dispatchers[index] = dispatcher;
                manager.Register(9814, checked((ushort)index), dispatcher);
            });
            Require(manager.ActiveStreamCount == count,
                "concurrent publication must retain the inline route and every named route");
            for (var index = 0; index < count; index++)
            {
                var dispatch = manager.DispatchChunkAsync(9814, checked((ushort)index), payload);
                Require(dispatch.IsCompletedSuccessfully,
                    "the synchronous fixture must dispatch without hidden waiters");
                dispatch.GetAwaiter().GetResult();
                Require(dispatchers[index].Dispatches == 1,
                    "each registered identity must address its own dispatcher");
            }
            manager.CompleteRequestStreams(9814, exception: null);
            Require(manager.ActiveStreamCount == 0, "request drain must release all concurrent registrations");
            foreach (var dispatcher in dispatchers)
                Require(dispatcher.Completions == 1, "every published route must complete once");
        }
        finally
        {
            manager.CompleteAll(exception: null);
        }
    }

    private static object? ReadNamedDictionary(StreamManager manager, long requestId)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var routes = typeof(StreamManager).GetField("_dispatchersByRequestId", flags)?.GetValue(manager)
            ?? throw new InvalidOperationException("The route fixture did not publish request routing state.");
        var routesType = routes.GetType();
        var entryType = routesType.GenericTypeArguments[0];
        var get = routesType.GetMethod("TryGetValue", flags, null,
            [typeof(long), entryType.MakeByRefType()], null)
            ?? throw new InvalidOperationException("The request map lookup was not found.");
        object?[] arguments = [requestId, null];
        Require((bool)get.Invoke(routes, arguments)!, "the fixture request must remain mapped");
        var request = arguments[1] ?? throw new InvalidOperationException("Request routing entry is missing.");
        var field = request.GetType().GetField("_byStreamId", flags)
            ?? throw new InvalidOperationException("Named route storage was not found.");
        return field.GetValue(request);
    }

    private sealed class RecordingDispatcher : IStreamDispatcher
    {
        private int _dispatches;
        private int _completions;
        internal int Dispatches => Volatile.Read(ref _dispatches);
        internal int Completions => Volatile.Read(ref _completions);
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        {
            _ = payload;
            Interlocked.Increment(ref _dispatches);
            return ValueTask.CompletedTask;
        }
        public void Complete(bool isError, string? errorMessage) => Interlocked.Increment(ref _completions);
        public void Complete(Exception? exception) => Interlocked.Increment(ref _completions);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
