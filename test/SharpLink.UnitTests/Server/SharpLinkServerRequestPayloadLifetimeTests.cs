using Microsoft.Extensions.Logging;
using SharpLink.Server;
using System.Buffers;
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SharpLink.UnitTests.Server;

public class SharpLinkServerRequestPayloadLifetimeTests
{
    private const long RequestId = 407;
    private static readonly Type ReviewTestsType = typeof(SharpLinkServerRequestScopeReviewTests);
    private static readonly Type LoggerFactoryType = GetNestedType("ScopeCaptureLoggerFactory");
    private static readonly Type StubBehaviorType = GetNestedType("StubBehavior");
    private static readonly Type ControlledStubType = GetNestedType("ControlledStub");
    private static readonly Type DispatchHarnessType = GetNestedType("DispatchHarness");
    private static readonly MethodInfo DispatchRequestMethod = typeof(SharpLinkServer).GetMethod(
        "DispatchRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new Exception("cannot find scoped Request dispatch path");

    [Test]
    public async Task PendingAsyncUnaryDoesNotRetainRawRequestBacking()
    {
        var loggerFactory = (ILoggerFactory)CreateInstance(LoggerFactoryType);
        var behavior = Enum.Parse(StubBehaviorType, "LogAfterSignal");
        var stub = CreateInstance(
            ControlledStubType,
            behavior,
            loggerFactory.CreateLogger("PayloadLifetimeService"),
            RpcMethodKind.Unary);
        var harnessObject = CreateInstance(DispatchHarnessType, loggerFactory, stub, false);
        var harness = (IAsyncDisposable)harnessObject;

        try
        {
            var server = (SharpLinkServer)GetProperty(harnessObject, "Server");
            var connection = (ServerConnectionState)GetProperty(harnessObject, "Connection");
            var (dispatch, backing) = StartPendingDispatch(server, connection, (IRpcStub)stub);

            await Assert.That(dispatch.IsCompleted).IsFalse();
            await AssertCollectedAsync(
                backing,
                "pending async request dispatch retained the raw PipeReader payload backing");

            ControlledStubType.GetMethod("Signal", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(stub, null);
            await dispatch;
        }
        finally
        {
            await harness.DisposeAsync();
            loggerFactory.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task Dispatch, WeakReference<byte[]> Backing) StartPendingDispatch(
        SharpLinkServer server,
        ServerConnectionState connection,
        IRpcStub stub)
    {
        var payload = new byte[1024 * 1024];
        BinaryPrimitives.WriteInt64LittleEndian(payload, stub.InterfaceHash);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(sizeof(long)), 1);
        var backing = new WeakReference<byte[]>(payload);
        var dispatch = (Task)DispatchRequestMethod.Invoke(
            server,
            [
                connection,
                RequestId,
                ProtocolV2FrameFlags.None,
                new ReadOnlySequence<byte>(payload),
                connection.CallCancellations,
                CancellationToken.None
            ])!;
        return (dispatch, backing);
    }

    private static async Task AssertCollectedAsync(WeakReference<byte[]> backing, string failureMessage)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            if (!IsAlive(backing))
                return;
            await Task.Yield();
        }

        throw new Exception(failureMessage);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<byte[]> backing)
        => backing.TryGetTarget(out _);

    private static Type GetNestedType(string name)
        => ReviewTestsType.GetNestedType(name, BindingFlags.NonPublic)
            ?? throw new Exception($"cannot find request-scope test fixture type {name}");

    private static object CreateInstance(Type type, params object?[] arguments)
        => Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: arguments,
            culture: null)
            ?? throw new Exception($"cannot create {type.Name}");

    private static object GetProperty(object instance, string name)
        => instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance)!;
}
