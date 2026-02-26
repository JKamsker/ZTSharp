using System.IO;

namespace ZTSharp.Tests;

public sealed class FileStateStoreSecurityTests
{
    [WindowsFact]
    public async Task WriteAsync_Rejects_WindowsDriveRootedPaths()
    {
        var path = TestTempPaths.CreateGuidSuffixed("zt-store-sec-");
        try
        {
            var store = new FileStateStore(path);

            await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync("C:/Windows/Temp/pwn", new byte[] { 1, 2, 3 }));

            Assert.Empty(Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [WindowsFact]
    public async Task WriteAsync_Rejects_NtfsAds()
    {
        var path = TestTempPaths.CreateGuidSuffixed("zt-store-sec-");
        try
        {
            var store = new FileStateStore(path);

            await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync("planet:ads", new byte[] { 1, 2, 3 }));

            Assert.Empty(Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_Rejects_RootedPaths()
    {
        var path = TestTempPaths.CreateGuidSuffixed("zt-store-sec-");
        try
        {
            var store = new FileStateStore(path);

            await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync("/etc/passwd", new byte[] { 1, 2, 3 }));

            Assert.Empty(Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_PlanetFallsBackToRoots_AndRootListContainsBothAliases()
    {
        var path = TestTempPaths.CreateGuidSuffixed("zt-store-alias-");
        try
        {
            var store = new FileStateStore(path);

            File.WriteAllBytes(Path.Combine(path, "roots"), new byte[] { 1, 2, 3, 4 });

            var readViaPlanet = await store.ReadAsync("planet");
            var listed = await store.ListAsync();

            Assert.NotNull(readViaPlanet);
            Assert.True(readViaPlanet!.Value.Span.SequenceEqual(new byte[] { 1, 2, 3, 4 }));
            Assert.Contains("planet", listed);
            Assert.Contains("roots", listed);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [WindowsFact]
    public async Task ListAsync_Rejects_WindowsDriveRootedPrefixes()
    {
        var path = TestTempPaths.CreateGuidSuffixed("zt-store-sec-");
        try
        {
            var store = new FileStateStore(path);

            await Assert.ThrowsAsync<ArgumentException>(() => store.ListAsync("C:/"));
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_IsAtomicReplace_BestEffort()
    {
        var path = TestTempPaths.CreateGuidSuffixed("zt-store-atomic-");
        try
        {
            var store = new FileStateStore(path);

            var oldBytes = Enumerable.Repeat((byte)0xAA, 256 * 1024).ToArray();
            var newBytes = Enumerable.Repeat((byte)0x55, 256 * 1024).ToArray();
            await store.WriteAsync("foo", oldBytes);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var writer = Task.Run(async () =>
            {
                for (var i = 0; i < 50; i++)
                {
                    var bytes = i % 2 == 0 ? newBytes : oldBytes;
                    await store.WriteAsync("foo", bytes, cts.Token);
                }
            }, cts.Token);

            var reader = Task.Run(async () =>
            {
                for (var i = 0; i < 250; i++)
                {
                    var read = await store.ReadAsync("foo", cts.Token);
                    if (read is null)
                    {
                        continue;
                    }

                    var span = read.Value.Span;
                    var matchesOld = span.SequenceEqual(oldBytes);
                    var matchesNew = span.SequenceEqual(newBytes);
                    Assert.True(matchesOld || matchesNew);
                }
            }, cts.Token);

            await Task.WhenAll(writer, reader);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }
}

