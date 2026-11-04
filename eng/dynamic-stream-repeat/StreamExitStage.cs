using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    // No stored plugin, Type, Exception, Task, delegate, stream or handle reference.
    // The supervisor exclusively owns this inherited ordinary-file descriptor.
    private static void StreamExitStage(int stage)
    {
        // Diagnostics must not replace an original body/Dispose exception. Only this
        // marker operation is best effort. The external classifier requires EVERY
        // expected marker (including cleanup and GC); missing/truncated data fails.
        try
        {
            var descriptor = Environment.GetEnvironmentVariable("SHARPLINK_STREAM_EXIT_STAGE_FD");
            if (descriptor is null)
                return;
            if (!int.TryParse(descriptor, NumberStyles.None, CultureInfo.InvariantCulture, out var fd) || fd < 3)
                return;
            var bytes = Encoding.ASCII.GetBytes(stage.ToString(CultureInfo.InvariantCulture) + " " +
                Stopwatch.GetTimestamp().ToString(CultureInfo.InvariantCulture) + "\n");
            var offset = 0;
            while (offset < bytes.Length)
            {
                var remaining = bytes.AsSpan(offset).ToArray();
                var written = WriteStage(fd, remaining, (nuint)remaining.Length);
                if (written < 0 && Marshal.GetLastPInvokeError() == 4)
                    continue;
                if (written <= 0 || written > remaining.Length)
                    return;
                offset += checked((int)written);
            }
        }
        catch
        {
            // No exception/reference is retained. Missing marker is an explicit
            // diagnostic qualification failure, never a successful case.
        }
    }

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern nint WriteStage(int fd, byte[] bytes, nuint count);
}
