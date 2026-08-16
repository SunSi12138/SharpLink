using System.Net.Sockets;
using SharpLink.Runtime;

namespace SharpLink.UnitTests.Runtime;

public class UnixSocketPermissionTests
{
    private const UnixFileMode OwnerAccess =
        UnixFileMode.UserRead |
        UnixFileMode.UserWrite;

    private const UnixFileMode GroupOrOtherAccess =
        UnixFileMode.GroupRead |
        UnixFileMode.GroupWrite |
        UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead |
        UnixFileMode.OtherWrite |
        UnixFileMode.OtherExecute;

    [Test]
    public async Task FilesystemUdsListenerShouldPublishOwnerOnlyPermissions()
    {
        if (OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(Path.GetTempPath(), $"sl-perm-{Guid.NewGuid():N}.sock");
        var listener = new SocketServerTransportListener(new UnixDomainSocketEndPoint(path));
        try
        {
            await Assert.That(File.Exists(path)).IsTrue();

            var mode = File.GetUnixFileMode(path);
            await Assert.That(mode & OwnerAccess).IsEqualTo(OwnerAccess);
            await Assert.That(mode & GroupOrOtherAccess).IsEqualTo((UnixFileMode)0);
        }
        finally
        {
            await listener.DisposeAsync();
            File.Delete(path);
        }

        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    public async Task PermissionHardeningFailureShouldFailClosedAndDeleteTheOwnedPath()
    {
        if (OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(Path.GetTempPath(), $"sl-perm-{Guid.NewGuid():N}.sock");
        Exception? failure = null;
        try
        {
            try
            {
                _ = new SocketServerTransportListener(
                    new UnixDomainSocketEndPoint(path),
                    backlog: 512,
                    options: null,
                    tlsOptions: null,
                    tlsHandshakeTimeout: null,
                    permissionHardeningOverride: static (_, _) =>
                        throw new UnauthorizedAccessException("injected hardening failure"));
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            await Assert.That(failure).IsTypeOf<UnauthorizedAccessException>();
            await Assert.That(File.Exists(path)).IsFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task PermissionHardeningShouldRejectAReplacedPathWithoutTouchingIt()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"sl-perm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "probe.sock");
            using (var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            {
                socket.Bind(new UnixDomainSocketEndPoint(path));
                var identity = UnixSocketPathIdentity.Capture(path);
                await Assert.That(identity.HasValue).IsTrue();

                File.Delete(path);
                await File.WriteAllTextAsync(path, "replacement-owned-by-caller");
                var replacementMode = File.GetUnixFileMode(path);

                var failure = CaptureFailure(() =>
                    SocketServerTransportListener.HardenUnixSocketPermissions(path, identity!.Value));

                await Assert.That(failure).IsTypeOf<IOException>();
                await Assert.That(await File.ReadAllTextAsync(path))
                    .IsEqualTo("replacement-owned-by-caller");
                await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(replacementMode);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task AbstractUdsListenerShouldBypassFilesystemPermissionHardening()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var invocations = 0;
        var listener = new SocketServerTransportListener(
            new UnixDomainSocketEndPoint("\0sharplink-abstract-perm"),
            backlog: 512,
            options: null,
            tlsOptions: null,
            tlsHandshakeTimeout: null,
            permissionHardeningOverride: (_, _) => invocations++);
        try
        {
            await Assert.That(listener.LocalEndPoint).IsNotNull();
            await Assert.That(invocations).IsEqualTo(0);
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    private static Exception? CaptureFailure(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
