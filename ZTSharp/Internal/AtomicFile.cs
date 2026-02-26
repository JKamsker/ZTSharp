namespace ZTSharp.Internal;

internal static class AtomicFile
{
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
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
                options: FileOptions.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            const int maxAttempts = 10;
            Exception? lastException = null;
            var moved = false;
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    File.Move(tmpPath, path, overwrite: true);
                    moved = true;
                    break;
                }
                catch (IOException ex) when (attempt < maxAttempts - 1)
                {
                    lastException = ex;
                    Thread.Sleep(TimeSpan.FromMilliseconds(10 * (attempt + 1)));
                }
                catch (UnauthorizedAccessException ex) when (attempt < maxAttempts - 1)
                {
                    lastException = ex;
                    Thread.Sleep(TimeSpan.FromMilliseconds(10 * (attempt + 1)));
                }
                catch (IOException ex)
                {
                    lastException = ex;
                    break;
                }
                catch (UnauthorizedAccessException ex)
                {
                    lastException = ex;
                    break;
                }
            }

            if (!moved)
            {
                throw new IOException($"Atomic replace failed after {maxAttempts} attempts: '{path}'.", lastException);
            }
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

            const int maxAttempts = 10;
            Exception? lastException = null;
            var moved = false;
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    File.Move(tmpPath, path, overwrite: true);
                    moved = true;
                    break;
                }
                catch (IOException ex) when (attempt < maxAttempts - 1)
                {
                    lastException = ex;
                    await Task.Delay(TimeSpan.FromMilliseconds(10 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException ex) when (attempt < maxAttempts - 1)
                {
                    lastException = ex;
                    await Task.Delay(TimeSpan.FromMilliseconds(10 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    lastException = ex;
                    break;
                }
                catch (UnauthorizedAccessException ex)
                {
                    lastException = ex;
                    break;
                }
            }

            if (!moved)
            {
                throw new IOException($"Atomic replace failed after {maxAttempts} attempts: '{path}'.", lastException);
            }
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
