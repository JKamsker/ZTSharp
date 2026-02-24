using Xunit;

namespace ZTSharp.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class E2eFactAttribute : FactAttribute
{
    public E2eFactAttribute(string? requiredEnvironmentVariable = null)
    {
        var flag = Environment.GetEnvironmentVariable("LIBZT_RUN_E2E");
        var enabled = bool.TryParse(flag, out var parsed) && parsed;
        if (!enabled)
        {
            Skip = "Set LIBZT_RUN_E2E=true to enable E2E tests.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(requiredEnvironmentVariable))
        {
            var value = Environment.GetEnvironmentVariable(requiredEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(value))
            {
                Skip = $"Set {requiredEnvironmentVariable} to run this E2E test.";
            }
        }
    }
}

