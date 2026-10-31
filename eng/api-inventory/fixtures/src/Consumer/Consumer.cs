namespace Fixture;

public class Derived : Base, IContract
{
    protected override int Number => 2;
    int IContract.Invoke() => Number;
#if WINDOWS
    public void WindowsOnly() { }
#endif
}

public partial record Packet(int Value);

public class NestedGenericDerived : Container.NestedBase<TypeArgument>, Container.INestedContract<TypeArgument>;

public unsafe class Signatures<T> where T : class, new()
{
    public required string Label { get; init; }
    public delegate* unmanaged[Cdecl]<int, int> Callback;
    public (string? Name, T[] Items) Convert(in int count, out int copied, string? suffix = null)
    {
        copied = count;
        return (suffix, new T[count]);
    }
}
