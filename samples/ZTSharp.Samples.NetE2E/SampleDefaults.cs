internal static class SampleDefaults
{
    public static string CreateGuidSuffixedTempPath(string prefix)
        => Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid());
}

