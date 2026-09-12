using SharpLink.Hosting;
using SharpLink.Runtime;

var property = typeof(IAnonymousPipeAllocatorAccessor).GetProperty(
    nameof(IAnonymousPipeAllocatorAccessor.AnonymousPipeAllocator));
if (property is null || property.PropertyType != typeof(IAnonymousPipeAllocator))
    throw new InvalidOperationException(
        "The Hosting package did not resolve the Runtime type exposed by IAnonymousPipeAllocatorAccessor.");

Console.WriteLine("HOSTING_PACKAGE_DIRECT_RUNTIME_DEPENDENCY_PASS");
