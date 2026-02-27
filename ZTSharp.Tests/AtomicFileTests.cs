using ZTSharp.Internal;

namespace ZTSharp.Tests;

public sealed class AtomicFileTests
{
    [Fact]
    public async Task WriteAllBytesAsync_Throws_WhenAtomicMoveNeverSucceeds()
    {
        var root = TestTempPaths.CreateGuidSuffixed("zt-atomic-file-");
        Directory.CreateDirectory(root);

        var destination = Path.Combine(root, "dest");
        Directory.CreateDirectory(destination);

        var ex = await Assert.ThrowsAsync<IOException>(async () =>
        {
            await AtomicFile.WriteAllBytesAsync(destination, new byte[] { 1, 2, 3 }, CancellationToken.None);
        });

        Assert.Contains("Atomic replace failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(destination));
    }
}

