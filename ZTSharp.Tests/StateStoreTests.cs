using System.IO;

namespace ZTSharp.Tests;

public sealed class StateStoreTests
{
    [Fact]
    public async Task FileStore_UsesRootsAlias()
    {
        var path = TestTempPaths.CreateGuidSuffixed("zt-store-alias-");
        try
        {
            var store = new FileStateStore(path);
            await store.WriteAsync("roots", new byte[] { 1, 2, 3, 4 });
            var readViaPlanet = await store.ReadAsync("planet");
            var listed = await store.ListAsync();

            Assert.NotNull(readViaPlanet);
            Assert.True(readViaPlanet!.Value.Span.SequenceEqual(new byte[] { 1, 2, 3, 4 }));
            Assert.Contains("roots", listed);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task MemoryStore_Roundtrip()
    {
        var store = new MemoryStateStore();
        await store.WriteAsync("foo/bar", new byte[] { 42, 43, 44 });
        var exists = await store.ExistsAsync("foo/bar");
        var value = await store.ReadAsync("foo/bar");
        var list = await store.ListAsync("foo");

        Assert.True(exists);
        Assert.True(value!.Value.Span.SequenceEqual(new byte[] { 42, 43, 44 }));
        Assert.Contains("foo/bar", list);

        var deleted = await store.DeleteAsync("foo/bar");
        var missing = await store.ReadAsync("foo/bar");

        Assert.True(deleted);
        Assert.Null(missing);
    }

    [Fact]
    public async Task MemoryStore_UsesRootsAlias()
    {
        var store = new MemoryStateStore();
        await store.WriteAsync("roots", new byte[] { 1, 2, 3, 4 });
        var readViaPlanet = await store.ReadAsync("planet");
        var listed = await store.ListAsync();

        Assert.NotNull(readViaPlanet);
        Assert.True(readViaPlanet!.Value.Span.SequenceEqual(new byte[] { 1, 2, 3, 4 }));
        Assert.Contains("roots", listed);
    }
}
