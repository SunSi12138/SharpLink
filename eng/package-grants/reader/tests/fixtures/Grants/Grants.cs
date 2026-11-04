using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Duplicated.Friend")]
[assembly: InternalsVisibleTo("Duplicated.Friend", AllInternalsVisible = true)]
[assembly: InternalsVisibleTo("Qualified.Friend, PublicKey=00000000000000000400000000000000", AllInternalsVisible = false)]
[assembly: IgnoresAccessChecksTo("Fixture.Target")]
[assembly: IgnoresAccessChecksTo("Fixture.Target")]

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute(string assemblyName) : Attribute
    {
        public string AssemblyName { get; } = assemblyName;
    }
}

public static class NeverExecuteFixture
{
    // Deliberately explosive initializer proves no fixture code executes.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize() => throw new InvalidOperationException("Target code was executed.");
}
