using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using LossRun.Api.Contracts;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace LossRun.Orchestration.Tests;

/// <summary>
/// Brief 24 step 1 (review plan §6, POC fixes): the two authenticators, offline. Entra tokens are signed with a local RSA key
/// served as the tenant's metadata; every failure is a null caller (the API answers 401), never an exception.
/// </summary>
public class AuthenticatorTests
{
    private const string Tenant = "00000000-0000-0000-0000-00000000aaaa";
    private const string Audience = "00000000-0000-0000-0000-00000000bbbb";
    private static readonly string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";
    private static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "test-key" };

    private static EntraAuthenticator Entra()
    {
        var config = new OpenIdConnectConfiguration { Issuer = Issuer };
        config.SigningKeys.Add(Key);
        return new EntraAuthenticator(Tenant, Audience, new StaticConfigurationManager<OpenIdConnectConfiguration>(config));
    }

    private static string Token(string audience = Audience, DateTime? expires = null, string[]? roles = null, string? oid = "user-oid-1", SecurityKey? key = null)
    {
        var claims = new List<Claim>();
        if (oid is not null)
        {
            claims.Add(new Claim("oid", oid));
        }

        claims.AddRange((roles ?? [Roles.Underwriter]).Select(r => new Claim("roles", r)));
        var now = DateTime.UtcNow;
        var expiry = expires ?? now.AddMinutes(30);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer, Audience = audience, Subject = new ClaimsIdentity(claims), NotBefore = expiry.AddMinutes(-60), IssuedAt = expiry.AddMinutes(-60), Expires = expiry,
            SigningCredentials = new SigningCredentials(key ?? Key, SecurityAlgorithms.RsaSha256),
        });
    }

    [Fact]
    public async Task A_valid_entra_token_yields_the_oid_and_the_highest_role()
    {
        var caller = await Entra().AuthenticateAsync("Bearer " + Token(roles: [Roles.Underwriter, Roles.UWManager]));

        Assert.Equal(new Caller("user-oid-1", Roles.UWManager), caller);
        Assert.Equal(Roles.Applicant, (await Entra().AuthenticateAsync("Bearer " + Token(audience: $"api://{Audience}", roles: [Roles.Applicant])))!.Role);
    }

    [Fact]
    public async Task Expired_wrong_audience_foreign_key_no_role_or_no_header_is_unauthenticated()
    {
        var entra = Entra();
        var foreign = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "other" };

        Assert.Null(await entra.AuthenticateAsync("Bearer " + Token(expires: DateTime.UtcNow.AddMinutes(-10))));
        Assert.Null(await entra.AuthenticateAsync("Bearer " + Token(audience: "some-other-api")));
        Assert.Null(await entra.AuthenticateAsync("Bearer " + Token(key: foreign)));
        Assert.Null(await entra.AuthenticateAsync("Bearer " + Token(roles: ["Reader"])));
        Assert.Null(await entra.AuthenticateAsync("Bearer " + Token(oid: null) + "x"));
        Assert.Null(await entra.AuthenticateAsync(null));
        Assert.Null(await entra.AuthenticateAsync("Basic dXNlcjpwYXNz"));
    }

    private static string Basic(string user, string password) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

    [Fact]
    public async Task Basic_users_authenticate_with_their_role_and_anything_else_is_unauthenticated()
    {
        var basic = new BasicAuthenticator("underwriter:s3cret-1:Underwriter; applicant:s3cret-2:Applicant");

        Assert.Equal(new Caller("underwriter", Roles.Underwriter), await basic.AuthenticateAsync(Basic("underwriter", "s3cret-1")));
        Assert.Equal(Roles.Applicant, (await basic.AuthenticateAsync(Basic("applicant", "s3cret-2")))!.Role);
        Assert.Null(await basic.AuthenticateAsync(Basic("underwriter", "wrong")));
        Assert.Null(await basic.AuthenticateAsync(Basic("nobody", "s3cret-1")));
        Assert.Null(await basic.AuthenticateAsync("Basic not-base64!"));
        Assert.Null(await basic.AuthenticateAsync("Bearer abc"));
        Assert.Null(await basic.AuthenticateAsync(null));
    }
}
