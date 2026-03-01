using System.Text;
using ZTSharp.Internal;

namespace ZTSharp.Tests;

public sealed class BoundedFileIOBomTests
{
    [Fact]
    public void TryReadAllText_StripsUtf8Bom()
    {
        var path = TestTempPaths.CreateGuidSuffixed("bounded-io-bom-");
        try
        {
            var payload = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("hello")).ToArray();
            File.WriteAllBytes(path, payload);

            Assert.True(BoundedFileIO.TryReadAllText(path, maxBytes: 1024, Encoding.UTF8, out var text));
            Assert.Equal("hello", text);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}

