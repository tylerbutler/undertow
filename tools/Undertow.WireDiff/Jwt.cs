using System.Security.Cryptography;
using System.Text;

namespace Undertow.WireDiff;

/// <summary>
/// Minimal HS256 minting matching signet's mint_token claim order:
/// documentId, tenantId, scopes, user, ver, iat, exp, jti.
/// </summary>
internal static class Jwt
{
    internal static string Mint(
        string tenant, string documentId, string[] scopes, string userId, string secret,
        long now, long expiresIn, string? userName = null)
    {
        var header = B64Url("""{"alg":"HS256","typ":"JWT"}"""u8.ToArray());
        var jti = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var scopeJson = string.Join(",", scopes.Select(s => $"\"{s}\""));
        var user = userName is null
            ? $"{{\"id\":\"{userId}\"}}"
            : $"{{\"id\":\"{userId}\",\"name\":{System.Text.Json.JsonSerializer.Serialize(userName)}}}";
        var payload = B64Url(Encoding.UTF8.GetBytes(
            $"{{\"documentId\":\"{documentId}\",\"tenantId\":\"{tenant}\",\"scopes\":[{scopeJson}]," +
            $"\"user\":{user},\"ver\":\"1.0\",\"iat\":{now},\"exp\":{now + expiresIn},\"jti\":\"{jti}\"}}"));
        var signed = $"{header}.{payload}";
        var sig = B64Url(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(signed)));
        return $"{signed}.{sig}";
    }

    private static string B64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
