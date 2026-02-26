namespace ZTSharp.Tests;

internal static class TestTempPaths
{
    public static string CreateGuidSuffixed(string prefix)
        => Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid());

    public static string CreateGuidSubdirectory(string baseDirectoryName)
        => Path.Combine(Path.GetTempPath(), baseDirectoryName, Guid.NewGuid().ToString("N"));
}

