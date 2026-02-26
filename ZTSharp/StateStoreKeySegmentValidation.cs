namespace ZTSharp;

internal static class StateStoreKeySegmentValidation
{
    private static readonly string[] ReservedDeviceNames =
    [
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9"
    ];

    public static void ValidateSegment(string segment, string originalPath, string paramName)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(originalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(paramName);

        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            if (c < 32)
            {
                throw new ArgumentException($"Invalid key path: {originalPath}", paramName);
            }

            if (c is '<' or '>' or '"' or ':' or '|' or '?' or '*')
            {
                throw new ArgumentException($"Invalid key path: {originalPath}", paramName);
            }
        }

        if (segment.EndsWith(' ') || segment.EndsWith('.'))
        {
            throw new ArgumentException($"Invalid key path: {originalPath}", paramName);
        }

        var trimmed = segment.TrimEnd(' ', '.');
        var baseName = trimmed;
        var dot = baseName.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            baseName = baseName.Substring(0, dot);
        }

        for (var i = 0; i < ReservedDeviceNames.Length; i++)
        {
            if (string.Equals(baseName, ReservedDeviceNames[i], StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Invalid key path: {originalPath}", paramName);
            }
        }
    }
}

