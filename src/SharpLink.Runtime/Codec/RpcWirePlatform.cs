namespace SharpLink.Runtime;

internal static class RpcWirePlatform
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Initialize()
        => EnsureSupported(BitConverter.IsLittleEndian);

    internal static bool IsSupported(bool isLittleEndian)
        => isLittleEndian;

    internal static void EnsureSupported(bool isLittleEndian)
    {
        if (isLittleEndian)
            return;

        throw new PlatformNotSupportedException(
            "SharpLink RPC wire codecs require a little-endian runtime.");
    }
}
