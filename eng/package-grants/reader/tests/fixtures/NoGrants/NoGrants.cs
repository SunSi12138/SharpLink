// Same short names in another namespace are not assembly access grants.
[assembly: Unusual.InternalsVisibleTo("Not.A.Friend")]
[assembly: Unusual.IgnoresAccessChecksTo("Not.A.Target")]
[assembly: Unusual.ReferenceAssembly]
namespace Unusual
{
    [AttributeUsage(AttributeTargets.Assembly)]
    internal sealed class InternalsVisibleToAttribute(string value) : Attribute { public string Value { get; } = value; }
    [AttributeUsage(AttributeTargets.Assembly)]
    internal sealed class IgnoresAccessChecksToAttribute(string value) : Attribute { public string Value { get; } = value; }
    [AttributeUsage(AttributeTargets.Assembly)]
    internal sealed class ReferenceAssemblyAttribute : Attribute { }
}

public sealed class NoGrantMarker;
