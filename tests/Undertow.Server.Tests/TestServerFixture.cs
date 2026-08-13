using Microsoft.AspNetCore.Mvc.Testing;

namespace Undertow.Server.Tests;

/// <summary>
/// Boots the real Program with test env: memory storage, dev secrets.
/// Config is read from the environment at startup (by design), so the vars are
/// set process-wide before the host builds.
/// </summary>
public sealed class TestServerFixture : WebApplicationFactory<Program>
{
    public const string JwtSecret = "test-jwt-secret";
    public const string MintSecret = "test-mint-secret";
    public const string Tenant = "fluid";

    static TestServerFixture()
    {
        Environment.SetEnvironmentVariable("UNDERTOW_JWT_SECRET", JwtSecret);
        Environment.SetEnvironmentVariable("UNDERTOW_TOKEN_MINT_SECRET", MintSecret);
        Environment.SetEnvironmentVariable("UNDERTOW_STORAGE_BACKEND", "memory");
        Environment.SetEnvironmentVariable("UNDERTOW_TENANT_ID", Tenant);
        Environment.SetEnvironmentVariable("UNDERTOW_PUBLIC_URL", "http://localhost");
    }

    public static string MintJwt(
        string documentId, string[]? scopes = null, long? now = null, long expiresIn = 3600)
    {
        var t = now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return Undertow.Protocol.AuthBoundary.mintToken(
            Tenant, documentId, scopes ?? ["doc:read", "doc:write", "summary:read", "summary:write"],
            "test-user", JwtSecret, t, expiresIn, "test-jti");
    }
}
