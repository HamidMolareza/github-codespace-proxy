namespace GhProxy.Api.Services;

internal static class CodespaceProxyRepositoryPolicy
{
    public static bool IsProxyRepository(string? repositoryFullName, string accountUsername)
    {
        if (string.IsNullOrWhiteSpace(repositoryFullName) || string.IsNullOrWhiteSpace(accountUsername))
        {
            return false;
        }

        var separatorIndex = repositoryFullName.IndexOf('/');
        if (separatorIndex <= 0 || separatorIndex == repositoryFullName.Length - 1)
        {
            return false;
        }

        var owner = repositoryFullName[..separatorIndex];
        var name = repositoryFullName[(separatorIndex + 1)..];
        return owner.Equals(accountUsername, StringComparison.OrdinalIgnoreCase) &&
               name.StartsWith("proxy", StringComparison.OrdinalIgnoreCase);
    }
}
