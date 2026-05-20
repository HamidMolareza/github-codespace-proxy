using GhProxy.Api.Services;

namespace GhProxy.Tests;

public sealed class SensitiveDataRedactorTests
{
    [Fact]
    public void Redact_RemovesProxyPasswordsAndTokens()
    {
        var redactor = new SensitiveDataRedactor();
        var input = "users admin:CL:proxy-secret token=abc123 ghp_123456789012345678901234567890";

        var output = redactor.Redact(input);

        Assert.DoesNotContain("proxy-secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", output, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_123456789012345678901234567890", output, StringComparison.Ordinal);
        Assert.Contains("<redacted>", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Snippet_RedactsAndBoundsOutput()
    {
        var redactor = new SensitiveDataRedactor();

        var output = redactor.Snippet("password=secret-value " + new string('x', 100), 32);

        Assert.DoesNotContain("secret-value", output, StringComparison.Ordinal);
        Assert.True(output.Length <= 47);
        Assert.Contains("truncated", output, StringComparison.Ordinal);
    }
}
