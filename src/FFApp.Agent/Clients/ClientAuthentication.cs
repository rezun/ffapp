using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FFApp.Agent.Clients;

/// <summary>Authenticates <c>Authorization: Bearer &lt;token&gt;</c> against <c>clients.json</c>.</summary>
public sealed class ClientTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    ClientRegistry registry) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public static readonly string SchemeName = "ClientToken";
    public static readonly string ClientIdClaim = "ffapp:client_id";
    private static readonly string BearerPrefix = "Bearer ";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());

        var client = registry.Authenticate(header[BearerPrefix.Length..].Trim());
        if (client is null)
            return Task.FromResult(AuthenticateResult.Fail("Unknown client token."));

        var identity = new ClaimsIdentity(
            [new Claim(ClientIdClaim, client.ClientId), new Claim(ClaimTypes.Name, client.Name)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

public static class ClientPrincipalExtensions
{
    public static string ClientId(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClientTokenAuthenticationHandler.ClientIdClaim)
        ?? throw new InvalidOperationException("The request is not authenticated as a client.");

    public static string ClientName(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.Name) ?? "";
}
