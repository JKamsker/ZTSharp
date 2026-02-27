using System.Text;

namespace ZTSharp.Internal;

internal static class BoundedFileIO
{
    public static bool TryReadAllBytes(string path, int maxBytes, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "Max bytes must be greater than zero.");
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16 * 1024,
                options: FileOptions.SequentialScan);

            long length;
            try
            {
                length = stream.Length;
            }
            catch (NotSupportedException)
            {
                return false;
            }

            if (length <= 0 || length > maxBytes || length > int.MaxValue)
            {
                return false;
            }

            var buffer = new byte[(int)length];

            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
                if (read == 0)
                {
                    return false;
                }

                totalRead += read;
            }

            bytes = buffer;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    public static bool TryReadAllText(string path, int maxBytes, Encoding encoding, out string text)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        text = string.Empty;

        if (!TryReadAllBytes(path, maxBytes, out var bytes))
        {
            return false;
        }

        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        text = reader.ReadToEnd();
        return true;
    }
}

