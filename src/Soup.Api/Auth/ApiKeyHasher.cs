using System.Security.Cryptography;
using System.Text;

namespace Soup.Api.Auth;

public static class ApiKeyHasher
{
    public static string Hash(string apiKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));

    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return "soup_" + Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
