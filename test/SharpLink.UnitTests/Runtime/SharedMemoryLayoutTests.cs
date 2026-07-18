using System.Security.Cryptography;

namespace SharpLink.UnitTests.Runtime;

public class SharedMemoryLayoutTests
{
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
    public async Task CreatingMappingShouldRemoveExclusivelyOpenableStaleFiles()
    {
        var directory = SharedMemoryMapping.GetMappingDirectory();
        Directory.CreateDirectory(directory);
        var stalePath = Path.Combine(directory, $"{Guid.NewGuid():N}.shm");
        await File.WriteAllBytesAsync(stalePath, [1, 2, 3]);
        var nonce = RandomNumberGenerator.GetBytes(SharedMemoryLayout.NonceBytes);

        await using var mapping = SharedMemoryMapping.CreateServer(64 * 1024, nonce, out _);

        await Assert.That(File.Exists(stalePath)).IsFalse();
    }
}
