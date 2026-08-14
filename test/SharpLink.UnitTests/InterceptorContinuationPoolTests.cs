using System.Reflection;
using System.Threading;
using SharpLink.Client;
using SharpLink.Server;

namespace SharpLink.UnitTests;

public sealed class InterceptorContinuationPoolTests
{
    [Test]
    public void ClientContinuationStateCacheShouldNotTransferOwnershipAcrossThreads()
        => AssertThreadLocalCache(
            typeof(SharpLinkClient),
            "ClientInterceptorState",
            "ClientContinuationState",
            "Client");

    [Test]
    public void ServerContinuationStateCacheShouldNotTransferOwnershipAcrossThreads()
        => AssertThreadLocalCache(
            typeof(SharpLinkServer),
            "ServerPipelineFacts",
            "ServerContinuationState",
            "Server");

    private static void AssertThreadLocalCache(
        Type rootType,
        string ownerTypeName,
        string stateTypeName,
        string component)
    {
        var ownerType = rootType.GetNestedType(ownerTypeName, BindingFlags.NonPublic)
            ?? throw new Exception($"cannot find {component} interceptor owner state");
        var stateType = ownerType.GetNestedType(stateTypeName, BindingFlags.NonPublic)
            ?? throw new Exception($"cannot find {component} continuation state");
        var rent = stateType.GetMethod("Rent", BindingFlags.Static | BindingFlags.Public)
            ?? throw new Exception($"cannot find {component} continuation Rent");
        var returnState = stateType.GetMethod("Return", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new Exception($"cannot find {component} continuation Return");
        var ownerValue = ownerType.IsValueType ? Activator.CreateInstance(ownerType) : null;

        object? first = null;
        object? sameThreadReuse = null;
        object? simultaneousSameThreadRent = null;
        var firstThreadFailure = default(Exception);
        var firstThread = new Thread(() =>
        {
            try
            {
                first = rent.Invoke(null, [ownerValue, 1])!;
                returnState.Invoke(first, null);
                sameThreadReuse = rent.Invoke(null, [ownerValue, 2])!;
                simultaneousSameThreadRent = rent.Invoke(null, [ownerValue, 3])!;
                returnState.Invoke(simultaneousSameThreadRent, null);
                returnState.Invoke(sameThreadReuse, null);
            }
            catch (Exception exception)
            {
                firstThreadFailure = Unwrap(exception);
            }
        })
        {
            IsBackground = true,
            Name = $"SharpLink {component} continuation cache owner A"
        };
        firstThread.Start();
        firstThread.Join();

        object? crossThreadRent = null;
        var secondThreadFailure = default(Exception);
        var secondThread = new Thread(() =>
        {
            try
            {
                crossThreadRent = rent.Invoke(null, [ownerValue, 4])!;
                returnState.Invoke(crossThreadRent, null);
            }
            catch (Exception exception)
            {
                secondThreadFailure = Unwrap(exception);
            }
        })
        {
            IsBackground = true,
            Name = $"SharpLink {component} continuation cache owner B"
        };
        secondThread.Start();
        secondThread.Join();

        Ensure(firstThreadFailure is null && secondThreadFailure is null,
            $"{component} continuation cache reflection failed: " +
            (firstThreadFailure ?? secondThreadFailure));
        Ensure(first is not null && ReferenceEquals(first, sameThreadReuse),
            $"{component} continuation state should retain same-thread single-slot reuse");
        Ensure(simultaneousSameThreadRent is not null &&
               !ReferenceEquals(sameThreadReuse, simultaneousSameThreadRent),
            $"{component} continuation cache must remove a rented state from its local slot");
        Ensure(crossThreadRent is not null && !ReferenceEquals(sameThreadReuse, crossThreadRent),
            $"{component} continuation state ownership must never transfer through a cross-thread cache");
    }

    private static Exception Unwrap(Exception exception)
        => exception is TargetInvocationException { InnerException: { } inner }
            ? inner
            : exception;

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
