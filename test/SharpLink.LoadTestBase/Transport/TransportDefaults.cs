using System;
using System.IO;

namespace SharpLink.LoadTestBase;

public static class TransportDefaults
{
    public static string GetDefaultUdsPath(string name)
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Path.GetTempPath(), $"{name}.sock");

        return $"/tmp/{name}.sock";
    }

    public static string GetDefaultPipeName(string name)
        => name;

    public static string GetDefaultSharedMemoryName(string name)
        => name;

    public static bool TryParseTransport(string value, out TransportMode mode)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "tcp":
                mode = TransportMode.Tcp;
                return true;
            case "uds":
                mode = TransportMode.Uds;
                return true;
            case "namedpipe":
            case "named-pipe":
            case "pipe":
                mode = TransportMode.NamedPipe;
                return true;
            case "anonymous":
            case "anonymouspipe":
            case "anonymous-pipe":
                mode = TransportMode.AnonymousPipe;
                return true;
            case "sharedmemory":
            case "shared-memory":
            case "shm":
                mode = TransportMode.SharedMemory;
                return true;
            default:
                mode = TransportMode.Tcp;
                return false;
        }
    }
}
