using Microsoft.AspNetCore.DataProtection;

namespace GhProxy.Api.Services;

public interface ISecretProtector
{
    string Protect(string value);
    string Unprotect(string value);
}

public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("proxy-node-secrets-v1");

    public string Protect(string value) => _protector.Protect(value);

    public string Unprotect(string value) => _protector.Unprotect(value);
}
