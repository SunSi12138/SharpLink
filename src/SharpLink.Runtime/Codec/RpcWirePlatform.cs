namespace SharpLink.Runtime;

internal static class RpcWirePlatform
{
#pragma warning disable CA2255 // Intentional process-wide wire ABI guard for this runtime library.
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Initialize()
        => EnsureSupported(BitConverter.IsLittleEndian);
#pragma warning restore CA2255

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
