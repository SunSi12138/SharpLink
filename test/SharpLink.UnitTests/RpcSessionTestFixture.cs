namespace SharpLink.UnitTests;

internal static class RpcSessionTestFixture
{
    internal static SharpLinkRuntimeContext RuntimeContext { get; } =
        new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);

    internal static RpcSessionCreationOptions ClientOptions(
        SharpLinkRuntimeContext? runtimeContext = null,
        RpcSessionFlushOptions? flushOptions = null)
        => new(
            RpcSessionRole.Client,
            runtimeContext ?? RuntimeContext,
            flushOptions);

    internal static RpcSessionCreationOptions ServerOptions(
        SharpLinkRuntimeContext? runtimeContext = null,
        RpcSessionFlushOptions? flushOptions = null,
        RpcSessionServiceExceptionMapper? serviceExceptionMapper = null)
        => new(
            RpcSessionRole.Server,
            runtimeContext ?? RuntimeContext,
            flushOptions,
            serviceExceptionMapper);
}
