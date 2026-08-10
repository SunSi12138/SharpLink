using SharpLink.Abstractions;

var property = typeof(SharpLinkGeneratedServiceDescriptor).GetProperty(
    nameof(SharpLinkGeneratedServiceDescriptor.Activator));
if (property is null || property.PropertyType != typeof(Func<IServiceProvider, object>))
    throw new InvalidOperationException(
        "The Abstractions package did not preserve the BCL IServiceProvider activator signature.");

Console.WriteLine("ABSTRACTIONS_PACKAGE_WITHOUT_DI_DEPENDENCY_PASS");
