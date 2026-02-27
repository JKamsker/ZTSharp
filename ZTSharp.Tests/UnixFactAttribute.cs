using Xunit;

namespace ZTSharp.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Unix only.";
        }
    }
}

