using Xunit;

namespace JKamsker.LibZt.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class ZeroTierE2eFactAttribute : FactAttribute
{
    public ZeroTierE2eFactAttribute(string? requiredEnvironmentVariable = null)
    {
        var flag = Environment.GetEnvironmentVariable("LIBZT_RUN_ZEROTIER_E2E");
        var enabled =
            string.Equals(flag, "1", StringComparison.Ordinal) ||
            (bool.TryParse(flag, out var parsed) && parsed);

        if (!enabled)
        {
            Skip = "Set LIBZT_RUN_ZEROTIER_E2E=1 to enable ZeroTier E2E tests.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(requiredEnvironmentVariable))
        {
            var value = Environment.GetEnvironmentVariable(requiredEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(value))
            {
                Skip = $"Set {requiredEnvironmentVariable} to run this ZeroTier E2E test.";
            }
        }
    }
}

