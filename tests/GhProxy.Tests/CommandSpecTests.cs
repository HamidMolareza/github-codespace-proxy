using GhProxy.Api.Services;

namespace GhProxy.Tests;

public sealed class CommandSpecTests
{
    [Fact]
    public void RedactedDisplay_HidesSensitiveArguments()
    {
        var command = new CommandSpec("ssh", ["echo", "users admin:CL:secret-password", "token=abc"]);

        var display = command.RedactedDisplay;

        Assert.DoesNotContain("secret-password", display, StringComparison.Ordinal);
        Assert.DoesNotContain("token=abc", display, StringComparison.Ordinal);
        Assert.Contains("<redacted>", display, StringComparison.Ordinal);
    }
}
