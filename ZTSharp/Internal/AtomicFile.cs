namespace ZTSharp.Internal;

internal static class AtomicFile
{
    public static async Task WriteAllBytesAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tmpPath = $"{path}.tmp.{Guid.NewGuid():N}";
        try
        {
            using (var stream = new FileStream(
                tmpPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                options: FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849
                stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
            }

            File.Move(tmpPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tmpPath))
                {
                    File.Delete(tmpPath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
