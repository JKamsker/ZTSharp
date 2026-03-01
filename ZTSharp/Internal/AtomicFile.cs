namespace ZTSharp.Internal;

internal static class AtomicFile
{
    private const UnixFileMode SecretFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

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

    public static void WriteSecretBytes(string path, ReadOnlySpan<byte> bytes)
    {
        if (OperatingSystem.IsWindows())
        {
            WriteAllBytes(path, bytes);
            return;
        }

        WriteAllBytesCore(path, bytes, SecretFileMode);
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

    public static Task WriteSecretBytesAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            return WriteAllBytesAsync(path, bytes, cancellationToken);
        }

        return WriteAllBytesCoreAsync(path, bytes, SecretFileMode, cancellationToken);
    }

    private static void WriteAllBytesCore(string path, ReadOnlySpan<byte> bytes, UnixFileMode unixCreateMode)
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
            using (var stream = CreateSecretWriteStream(tmpPath, unixCreateMode, asynchronous: false))
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

    private static async Task WriteAllBytesCoreAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        UnixFileMode unixCreateMode,
        CancellationToken cancellationToken)
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
            using (var stream = CreateSecretWriteStream(tmpPath, unixCreateMode, asynchronous: true))
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

    private static FileStream CreateSecretWriteStream(string path, UnixFileMode unixCreateMode, bool asynchronous)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = 16 * 1024,
                    Options = asynchronous ? FileOptions.Asynchronous : FileOptions.None,
                    UnixCreateMode = unixCreateMode
                };

                return new FileStream(path, options);
            }
            catch (PlatformNotSupportedException)
            {
            }
            catch (NotSupportedException)
            {
            }
        }

        var fallback = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            options: asynchronous ? FileOptions.Asynchronous : FileOptions.None);

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, unixCreateMode);
            }
        }
        catch (PlatformNotSupportedException)
        {
            fallback.Dispose();
            TryDeleteFallback(path);
            throw new IOException("Failed to set secret file permissions.");
        }
        catch (NotSupportedException)
        {
            fallback.Dispose();
            TryDeleteFallback(path);
            throw new IOException("Failed to set secret file permissions.");
        }
        catch (IOException ex)
        {
            fallback.Dispose();
            throw new IOException("Failed to set secret file permissions.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            fallback.Dispose();
            throw new IOException("Failed to set secret file permissions.", ex);
        }

        return fallback;
    }

    private static void TryDeleteFallback(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
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
