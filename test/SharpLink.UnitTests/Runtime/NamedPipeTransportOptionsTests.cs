using System.IO.Pipes;
using SharpLink.Client;
using SharpLink.Server;

namespace SharpLink.UnitTests.Runtime;

public class NamedPipeTransportOptionsTests
{
    [Test]
    public async Task OptionsShouldDefaultToCurrentUserOnly()
    {
        var pipeOptions = new NamedPipeTransportOptions().ToPipeOptions();

        await Assert.That(pipeOptions.HasFlag(PipeOptions.Asynchronous)).IsTrue();
        await Assert.That(pipeOptions.HasFlag(PipeOptions.CurrentUserOnly)).IsTrue();
    }

    [Test]
    public async Task OptionsShouldAllowExplicitCrossUserAccess()
    {
        var pipeOptions = new NamedPipeTransportOptions
        {
            AllowCrossUserAccess = true
        }.ToPipeOptions();

        await Assert.That(pipeOptions.HasFlag(PipeOptions.Asynchronous)).IsTrue();
        await Assert.That(pipeOptions.HasFlag(PipeOptions.CurrentUserOnly)).IsFalse();
    }

    [Test]
    public async Task ClientFactoryShouldDefaultToCurrentUserOnly()
    {
        await using var factory = new NamedPipeClientTransportFactory($"np{Guid.NewGuid():N}");

        await Assert.That(factory.EffectivePipeOptions.HasFlag(PipeOptions.CurrentUserOnly)).IsTrue();
        await Assert.That(factory.EffectivePipeOptions.HasFlag(PipeOptions.Asynchronous)).IsTrue();
    }

    [Test]
    public async Task ServerListenerShouldDefaultToCurrentUserOnly()
    {
        await using var listener = new NamedPipeServerTransportListener($"np{Guid.NewGuid():N}");

        await Assert.That(listener.EffectivePipeOptions.HasFlag(PipeOptions.CurrentUserOnly)).IsTrue();
        await Assert.That(listener.EffectivePipeOptions.HasFlag(PipeOptions.Asynchronous)).IsTrue();
    }

    [Test]
    public async Task ServerBuilderShouldDefaultToCurrentUserOnly()
    {
        var builder = SharpLinkServerBuilder.Create().UseNamedPipe($"np{Guid.NewGuid():N}");
        var listener = builder.Transport as NamedPipeServerTransportListener;

        await Assert.That(listener).IsNotNull();
        await Assert.That(listener!.EffectivePipeOptions.HasFlag(PipeOptions.CurrentUserOnly)).IsTrue();
    }

    [Test]
    public async Task ServerBuilderShouldAllowExplicitCrossUserAccess()
    {
        var builder = SharpLinkServerBuilder.Create().UseNamedPipe(
            $"np{Guid.NewGuid():N}",
            options => options.AllowCrossUserAccess = true);
        var listener = builder.Transport as NamedPipeServerTransportListener;

        await Assert.That(listener).IsNotNull();
        await Assert.That(listener!.EffectivePipeOptions.HasFlag(PipeOptions.CurrentUserOnly)).IsFalse();
    }

    [Test]
    public async Task ClientBuilderShouldDefaultToCurrentUserOnly()
    {
        var builder = SharpClientBuilder.Create().UseNamedPipe($"np{Guid.NewGuid():N}");
        var factory = builder.FixedTransportFactory as NamedPipeClientTransportFactory;

        await Assert.That(factory).IsNotNull();
        await Assert.That(factory!.EffectivePipeOptions.HasFlag(PipeOptions.CurrentUserOnly)).IsTrue();
    }

    [Test]
    public async Task ClientBuilderShouldAllowExplicitCrossUserAccess()
    {
        var builder = SharpClientBuilder.Create().UseNamedPipe(
            $"np{Guid.NewGuid():N}",
            options => options.AllowCrossUserAccess = true);
        var factory = builder.FixedTransportFactory as NamedPipeClientTransportFactory;

        await Assert.That(factory).IsNotNull();
        await Assert.That(factory!.EffectivePipeOptions.HasFlag(PipeOptions.CurrentUserOnly)).IsFalse();
    }

    [Test]
    public async Task EndpointFactoryShouldAllowExplicitCrossUserAccess()
    {
        var endpointFactory = SharpLinkTransportFactories.NamedPipes(
            options => options.AllowCrossUserAccess = true);
        var factory = endpointFactory(new SharpLinkEndpoint
        {
            Id = $"np{Guid.NewGuid():N}",
            Address = new SharpLinkNamedPipeAddress($"np{Guid.NewGuid():N}")
        }) as NamedPipeClientTransportFactory;

        await Assert.That(factory).IsNotNull();
        await Assert.That(factory!.EffectivePipeOptions.HasFlag(PipeOptions.CurrentUserOnly)).IsFalse();
    }
}
