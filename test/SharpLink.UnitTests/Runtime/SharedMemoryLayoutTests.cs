using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class SharedMemoryLayoutTests
{
    // Marked tests share the process-wide mapping counter or fixed shared-memory directory state.
    [Test]
    public async Task RingCursorArithmeticShouldSurviveSignedOverflow()
    {
        var nonce = RandomNumberGenerator.GetBytes(SharedMemoryLayout.NonceBytes);
        await using var mapping = SharedMemoryMapping.CreateServer(64 * 1024, nonce, out _);
        var direction = SharedMemoryLayout.GetDirection(mapping, clientToServer: true);
        var read = long.MaxValue - 31;
        var write = unchecked(read + 64);

        direction.PublishReadPosition(read);
        direction.PublishWritePosition(write);

        await Assert.That(direction.GetAvailableBytes(write, read)).IsEqualTo(64);
    }

    [Test]
    public async Task LayoutValidationShouldRejectNonceMismatch()
    {
        var nonce = RandomNumberGenerator.GetBytes(SharedMemoryLayout.NonceBytes);
        var wrongNonce = RandomNumberGenerator.GetBytes(SharedMemoryLayout.NonceBytes);
        await using var mapping = SharedMemoryMapping.CreateServer(64 * 1024, nonce, out _);

        await Assert.That(() => SharedMemoryLayout.Validate(mapping, 64 * 1024, wrongNonce))
            .Throws<SharpLinkException>();
    }

    [Test]
    [NotInParallel]
    public async Task ConcurrentMappingCreationShouldNotUnlinkLivePeers()
    {
        const int capacity = 64 * 1024;
        for (var round = 0; round < 64; round++)
        {
            using var start = new ManualResetEventSlim(initialState: false);
            var mappings = new ConcurrentBag<(SharedMemoryMapping Mapping, string Path, byte[] Nonce)>();
            var creators = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                var nonce = RandomNumberGenerator.GetBytes(SharedMemoryLayout.NonceBytes);
                start.Wait();
                var mapping = SharedMemoryMapping.CreateServer(capacity, nonce, out var path);
                mappings.Add((mapping, path, nonce));
            })).ToArray();
            start.Set();
            try
            {
                await Task.WhenAll(creators);
                foreach (var item in mappings)
                {
                    await Assert.That(File.Exists(item.Path)).IsTrue();
                    await using var client = SharedMemoryMapping.OpenClient(item.Path, capacity, item.Nonce);
                }
            }
            finally
            {
                foreach (var item in mappings)
                    await item.Mapping.DisposeAsync();
            }
        }
    }

    [Test]
    [NotInParallel]
    public async Task OpeningInvalidMappingShouldReleaseMappedView()
    {
        const int capacity = 64 * 1024;
        var nonce = RandomNumberGenerator.GetBytes(SharedMemoryLayout.NonceBytes);
        var wrongNonce = RandomNumberGenerator.GetBytes(SharedMemoryLayout.NonceBytes);
        var baseline = SharedMemoryMapping.ActiveMappingCount;
        var serverMapping = SharedMemoryMapping.CreateServer(capacity, nonce, out var path);
        try
        {
            await Assert.That(SharedMemoryMapping.ActiveMappingCount).IsEqualTo(baseline + 1);
            for (var attempt = 0; attempt < 32; attempt++)
            {
                try
                {
                    var unexpected = SharedMemoryMapping.OpenClient(path, capacity, wrongNonce);
                    await unexpected.DisposeAsync();
                    throw new Exception("expected invalid shared-memory mapping rejection");
                }
                catch (SharpLinkException exception)
                {
                    await Assert.That(exception.Code).IsEqualTo(SharpLinkErrorCode.FailedPrecondition);
                }

                await Assert.That(SharedMemoryMapping.ActiveMappingCount).IsEqualTo(baseline + 1);
            }
        }
        finally
        {
            await serverMapping.DisposeAsync();
        }

        await Assert.That(SharedMemoryMapping.ActiveMappingCount).IsEqualTo(baseline);
    }

    [Test]
    [NotInParallel]
    public async Task MappingFileShouldDisappearAfterBothSidesClose()
    {
        const int capacity = 64 * 1024;
        var nonce = RandomNumberGenerator.GetBytes(SharedMemoryLayout.NonceBytes);
        var server = SharedMemoryMapping.CreateServer(capacity, nonce, out var path);
        var client = SharedMemoryMapping.OpenClient(path, capacity, nonce);
        try
        {
            server.UnlinkAfterClientOpened();
            await server.DisposeAsync();
            await client.DisposeAsync();

            await Assert.That(File.Exists(path)).IsFalse();
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    [Test]
    public async Task MappingPathShouldRejectLocationsOutsideTransportDirectory()
    {
        var outsidePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.shm");
        try
        {
            SharedMemoryMapping.ValidateMappingPath(outsidePath);
            throw new Exception("expected mapping path rejection");
        }
        catch (SharpLinkException exception)
        {
            await Assert.That(exception.Code).IsEqualTo(SharpLinkErrorCode.PermissionDenied);
        }
    }

    [Test]
    public async Task MappingDirectoryShouldBeScopedToTheCurrentUser()
    {
        var directory = SharedMemoryMapping.GetMappingDirectory();
        var processWideDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sharplink-shm"));

        await Assert.That(string.Equals(directory, processWideDirectory, StringComparison.Ordinal)).IsFalse();
        await Assert.That(Path.GetFileName(directory).StartsWith("sharplink-shm-", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    [NotInParallel]
    public async Task CreatingMappingShouldRemoveExclusivelyOpenableStaleFiles()
    {
        var directory = SharedMemoryMapping.GetMappingDirectory();
        Directory.CreateDirectory(directory);
        var stalePath = Path.Combine(directory, $"{Guid.NewGuid():N}.shm");
        await File.WriteAllBytesAsync(stalePath, [1, 2, 3]);
        File.SetLastWriteTimeUtc(stalePath, DateTime.UtcNow - TimeSpan.FromMinutes(5));
        var nonce = RandomNumberGenerator.GetBytes(SharedMemoryLayout.NonceBytes);

        await using var mapping = SharedMemoryMapping.CreateServer(64 * 1024, nonce, out _);

        await Assert.That(File.Exists(stalePath)).IsFalse();
    }

    [Test]
    [NotInParallel]
    public async Task CreatingMappingShouldPreserveFreshPeerFiles()
    {
        var directory = SharedMemoryMapping.GetMappingDirectory();
        Directory.CreateDirectory(directory);
        var freshPath = Path.Combine(directory, $"{Guid.NewGuid():N}.shm");
        await File.WriteAllBytesAsync(freshPath, [1, 2, 3]);
        var nonce = RandomNumberGenerator.GetBytes(SharedMemoryLayout.NonceBytes);
        try
        {
            await using var mapping = SharedMemoryMapping.CreateServer(64 * 1024, nonce, out _);
            await Assert.That(File.Exists(freshPath)).IsTrue();
        }
        finally
        {
            File.Delete(freshPath);
        }
    }
}
