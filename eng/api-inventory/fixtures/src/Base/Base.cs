using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Fixture.Consumer")]

namespace Fixture;

public class Base
{
    protected virtual int Number => 1;
    private protected void Restricted() { }
    protected internal void FamilyOrAssembly() { }
    protected class ProtectedNested;
}

internal class InternalContainer
{
    public class PublicNested;
}

public interface IContract { int Invoke(); }
public class SharedName;
public class SharedName<T>;

public class TypeArgument;
public class Container
{
    public class NestedBase<T>;
    public interface INestedContract<T>;
}

public static class Extensions
{
    extension(Base receiver)
    {
        public int Extra => 2;
    }
}
