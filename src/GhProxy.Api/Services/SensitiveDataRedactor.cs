using System.Text.RegularExpressions;

namespace GhProxy.Api.Services;

public interface ISensitiveDataRedactor
{
    string Redact(string? value);
    string Snippet(string? value, int maxChars);
}

public sealed partial class SensitiveDataRedactor : ISensitiveDataRedactor
{
    public string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var redacted = UsersDirectiveRegex().Replace(value, "users <redacted>");
        redacted = KeyValueSecretRegex().Replace(redacted, "$1=<redacted>");
        redacted = GitHubTokenRegex().Replace(redacted, "<redacted-token>");
        redacted = BearerRegex().Replace(redacted, "$1 <redacted>");
        redacted = UrlCredentialRegex().Replace(redacted, "$1//<redacted>@");
        return redacted;
    }

    public string Snippet(string? value, int maxChars)
    {
        var redacted = Redact(value);
        if (maxChars <= 0 || redacted.Length <= maxChars)
        {
            return redacted;
        }

        return string.Concat(redacted.AsSpan(0, maxChars), "... <truncated>");
    }

    [GeneratedRegex(@"users\s+.+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsersDirectiveRegex();

    [GeneratedRegex(@"(?i)\b(password|passwd|pass|token|pat|secret|authorization|proxyPassword)\b\s*[:=]\s*([^;\s]+)")]
    private static partial Regex KeyValueSecretRegex();

    [GeneratedRegex(@"gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubTokenRegex();

    [GeneratedRegex(@"(?i)\b(Bearer|Basic)\s+[A-Za-z0-9+/=_\-.]+")]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"(https?|socks5h?)://[^/\s:@]+:[^/\s@]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredentialRegex();
}
