namespace SharpLink.Server;


public record ServiceEntry(IRpcStub Stub, Type ServiceType, IServiceProvider ServiceProvider);