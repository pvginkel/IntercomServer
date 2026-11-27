using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace IntercomServer;

/// <summary>The client roles of <see cref="SignInConfiguration.Audience"/> a route can ask for.</summary>
internal static class Roles
{
    /// <summary>Jenkins' service account, which publishes app releases.</summary>
    public const string Pipeline = "pipeline";
}

/// <summary>
/// JWT bearer sign-in against the Keycloak realm of <see cref="SignInConfiguration"/>. A token's
/// roles are its client roles on the audience's client alone.
/// </summary>
internal static class SignIn
{
    public static IServiceCollection AddSignIn(
        this IServiceCollection services,
        SignInConfiguration configuration
    )
    {
        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = configuration.Issuer;
                options.MapInboundClaims = false;
                options.TokenValidationParameters.ValidIssuer = configuration.Issuer;
                options.TokenValidationParameters.ValidAudience = configuration.Audience;
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        context.Principal = WithClientRoles(context.Principal!, configuration.Audience);
                        return Task.CompletedTask;
                    },
                };
            });
        services.AddAuthorization();

        // AddAuthentication registers data protection, whose hosted service loads its key ring at
        // the start: it makes a key and warns that the key is stored unencrypted. A bearer token
        // is validated without it, and nothing else here protects data.
        services.Remove(
            services.Single(service =>
                service.ServiceType == typeof(IHostedService)
                && service.ImplementationType?.FullName
                    == "Microsoft.AspNetCore.DataProtection.Internal.DataProtectionHostedService"
            )
        );

        return services;
    }

    // Keycloak puts the roles of every client the holder has in resource_access, and the realm's
    // in realm_access: only the audience's client's are this server's. The principal keeps the
    // token's claims but any of the role claim type, and takes those roles.
    private static ClaimsPrincipal WithClientRoles(ClaimsPrincipal principal, string client)
    {
        var claims = principal.Claims.Where(claim => claim.Type != ClaimTypes.Role).ToList();
        claims.AddRange(
            ClientRoles(principal.FindFirst("resource_access")?.Value, client)
                .Select(role => new Claim(ClaimTypes.Role, role))
        );

        return new ClaimsPrincipal(
            new ClaimsIdentity(
                claims,
                JwtBearerDefaults.AuthenticationScheme,
                "preferred_username",
                ClaimTypes.Role
            )
        );
    }

    private static List<string> ClientRoles(string? resourceAccess, string client)
    {
        if (resourceAccess == null)
            return [];

        using var document = JsonDocument.Parse(resourceAccess);
        if (
            document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty(client, out var access)
            || access.ValueKind != JsonValueKind.Object
            || !access.TryGetProperty("roles", out var roles)
            || roles.ValueKind != JsonValueKind.Array
        )
            return [];

        return roles
            .EnumerateArray()
            .Where(role => role.ValueKind == JsonValueKind.String)
            .Select(role => role.GetString()!)
            .ToList();
    }
}
