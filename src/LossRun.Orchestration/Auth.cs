using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using LossRun.Api.Contracts;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace LossRun.Orchestration;

/// <summary>Authenticated caller: user id (Entra oid / dev user name) and app role.</summary>
public sealed record Caller(string UserId, string Role)
{
    public bool IsReviewer => Roles.CanReview(Role);

    /// <summary>Brief 26: reviewers and the Engineer role (read, never decide).</summary>
    public bool IsReader => Roles.CanRead(Role);
    public bool SeesAllUploads => Role is not Roles.Applicant;
}

public interface IAuthenticator
{
    /// <summary>Null when the request is not authenticated (the API answers 401; it is never anonymous).</summary>
    Task<Caller?> AuthenticateAsync(string? authorizationHeader, CancellationToken cancellationToken = default);
}

/// <summary>
/// Entra ID bearer tokens (brief 07: app roles Applicant / Underwriter / UWManager / Admin). Validates issuer,
/// audience, lifetime and signature against the tenant's OIDC metadata; the first known role in <c>roles</c> wins
/// (highest privilege first). Uses Microsoft.IdentityModel directly, the libraries under Microsoft.Identity.Web.
/// </summary>
public sealed class EntraAuthenticator : IAuthenticator
{
    private static readonly string[] RoleOrder = [Roles.Admin, Roles.UWManager, Roles.Underwriter, Roles.Engineer, Roles.Applicant, Roles.QuoteSystem];

    private readonly string tenantId;
    private readonly string audience;
    private readonly IConfigurationManager<OpenIdConnectConfiguration> _metadata;

    public EntraAuthenticator(string tenantId, string audience)
        : this(tenantId, audience, new ConfigurationManager<OpenIdConnectConfiguration>(
            $"https://login.microsoftonline.com/{tenantId}/v2.0/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever()))
    {
    }

    /// <summary>Brief 24 step 1: the tenant metadata from a given source (tests sign tokens with a local key, offline).</summary>
    internal EntraAuthenticator(string tenantId, string audience, IConfigurationManager<OpenIdConnectConfiguration> metadata)
    {
        (this.tenantId, this.audience, _metadata) = (tenantId, audience, metadata);
    }

    private readonly JsonWebTokenHandler _handler = new();

    public async Task<Caller?> AuthenticateAsync(string? authorizationHeader, CancellationToken cancellationToken = default)
    {
        if (authorizationHeader is null || !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var config = await _metadata.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var result = await _handler.ValidateTokenAsync(authorizationHeader["Bearer ".Length..].Trim(), new TokenValidationParameters
        {
            ValidIssuers = [config.Issuer, $"https://sts.windows.net/{tenantId}/"],
            ValidAudiences = [audience, $"api://{audience}"],
            IssuerSigningKeys = config.SigningKeys,
            ValidateLifetime = true,
        }).ConfigureAwait(false);
        if (!result.IsValid)
        {
            return null;
        }

        var roles = result.ClaimsIdentity.FindAll("roles").Select(c => c.Value).Concat(result.ClaimsIdentity.FindAll(ClaimTypes.Role).Select(c => c.Value)).ToHashSet();
        var role = RoleOrder.FirstOrDefault(roles.Contains);
        var user = result.ClaimsIdentity.FindFirst("oid")?.Value ?? result.ClaimsIdentity.FindFirst("sub")?.Value;
        return role is null || user is null ? null : new Caller(user, role);
    }
}

/// <summary>
/// <c>LOSSRUN_DEV_AUTH=basic</c> for the external demo host: HTTP Basic against a user list
/// <c>name:password:Role;…</c> (configuration, from Key Vault — never committed). Constant-time comparison.
/// </summary>
public sealed class BasicAuthenticator : IAuthenticator
{
    private readonly Dictionary<string, (byte[] Hash, string Role)> _users;

    public BasicAuthenticator(string users)
    {
        _users = users.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(u => u.Split(':', 3))
            .Where(p => p.Length == 3)
            .ToDictionary(p => p[0], p => (SHA256.HashData(Encoding.UTF8.GetBytes(p[1])), p[2]), StringComparer.Ordinal);
    }

    public Task<Caller?> AuthenticateAsync(string? authorizationHeader, CancellationToken cancellationToken = default)
    {
        if (authorizationHeader is null || !authorizationHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<Caller?>(null);
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorizationHeader["Basic ".Length..].Trim()));
        }
        catch (FormatException)
        {
            return Task.FromResult<Caller?>(null);
        }

        var colon = decoded.IndexOf(':');
        if (colon <= 0 || !_users.TryGetValue(decoded[..colon], out var user))
        {
            return Task.FromResult<Caller?>(null);
        }

        var ok = CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(decoded[(colon + 1)..])), user.Hash);
        return Task.FromResult(ok ? new Caller(decoded[..colon], user.Role) : null);
    }
}
