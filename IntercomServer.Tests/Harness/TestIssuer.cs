using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace IntercomServer.Tests.Harness;

/// <summary>
/// The suite's Keycloak realm: an issuer on loopback, over HTTPS with a certificate of its own,
/// that serves a realm's discovery document and signing keys, and mints the tokens the tests
/// send, signed by a key the suite makes. The server is pointed at it as its issuer
/// (<c>KEYCLOAK_ISSUER</c>), and validates its tokens as it validates Keycloak's.
/// </summary>
internal sealed class TestIssuer : IAsyncDisposable
{
    /// <summary>The server's client, the audience it checks unless told otherwise.</summary>
    public const string ServerClient = "intercom-server";

    private const string KeyId = "intercom-server-tests";

    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = KeyId };

    // The same key id as the issuer's, so a token signed by it reads as the issuer's until its
    // signature is checked.
    private static readonly RsaSecurityKey ForeignKey = new(RSA.Create(2048)) { KeyId = KeyId };

    private static readonly X509Certificate2 Certificate = CreateCertificate();

    private readonly IHost _host;

    private TestIssuer(IHost host, string issuer)
    {
        _host = host;
        Issuer = issuer;
    }

    /// <summary>The realm's issuer URL, the <c>iss</c> of its tokens.</summary>
    public string Issuer { get; }

    public static async Task<TestIssuer> StartAsync()
    {
        string? issuer = null;
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
                web.UseKestrel(kestrel =>
                        kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(Certificate))
                    )
                    .Configure(app => app.Run(context => ServeAsync(context, issuer!)))
            )
            .Build();

        using var timeout = new CancellationTokenSource(Timeouts.Observe);
        await host.StartAsync(timeout.Token);

        var address = host.Services.GetRequiredService<IServer>()
            .Features.GetRequiredFeature<IServerAddressesFeature>()
            .Addresses.Single();
        issuer = $"https://{IPAddress.Loopback}:{new Uri(address).Port}/realms/tests";

        return new TestIssuer(host, issuer);
    }

    // What the server reads of a Keycloak realm: its discovery document, and the signing keys
    // that names.
    private static async Task ServeAsync(HttpContext context, string issuer)
    {
        var realm = new Uri(issuer).AbsolutePath;
        if (context.Request.Path == $"{realm}/.well-known/openid-configuration")
            await context.Response.WriteAsJsonAsync(
                new Dictionary<string, string>
                {
                    ["issuer"] = issuer,
                    ["jwks_uri"] = $"{issuer}/protocol/openid-connect/certs",
                }
            );
        else if (context.Request.Path == $"{realm}/protocol/openid-connect/certs")
            await context.Response.WriteAsJsonAsync(new { keys = new[] { PublicJwk(SigningKey) } });
        else
            context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    /// <summary>
    /// The server's channel to the issuer, which trusts the issuer's certificate alone, as
    /// production's trusts the CAs that sign Keycloak's.
    /// </summary>
    public static HttpMessageHandler CreateBackchannel() =>
        new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate != null && certificate.RawData.AsSpan().SequenceEqual(Certificate.RawData),
        };

    /// <summary>A valid token holding <paramref name="roles"/> on the server's client.</summary>
    public string TokenWithRoles(params string[] roles) =>
        CreateToken(clientRoles: new Dictionary<string, string[]> { [ServerClient] = roles });

    /// <summary>
    /// A token as Keycloak makes it: <paramref name="clientRoles"/> in <c>resource_access</c>,
    /// <paramref name="realmRoles"/> in <c>realm_access</c>, for <paramref name="audiences"/>
    /// (the server's client by default), from <paramref name="issuer"/> (this realm by default),
    /// expiring after <paramref name="expiresIn"/> (five minutes by default; negative for one
    /// expired that long ago), and signed by this realm's key unless
    /// <paramref name="signedByIssuer"/> is false.
    /// </summary>
    public string CreateToken(
        IReadOnlyDictionary<string, string[]>? clientRoles = null,
        IReadOnlyList<string>? realmRoles = null,
        IReadOnlyList<string>? audiences = null,
        string? issuer = null,
        TimeSpan? expiresIn = null,
        bool signedByIssuer = true
    )
    {
        var expires = DateTime.UtcNow + (expiresIn ?? TimeSpan.FromMinutes(5));
        var issuedAt = expires - TimeSpan.FromMinutes(10);
        var claims = new Dictionary<string, object>
        {
            ["aud"] = (audiences ?? [ServerClient]).ToList(),
            ["sub"] = Guid.NewGuid().ToString(),
            ["typ"] = "Bearer",
            ["azp"] = "intercom-client",
            ["preferred_username"] = "tester",
            ["resource_access"] = (clientRoles ?? new Dictionary<string, string[]>()).ToDictionary(
                client => client.Key,
                client => (object)new Dictionary<string, object> { ["roles"] = client.Value.ToList() }
            ),
            ["realm_access"] = new Dictionary<string, object> { ["roles"] = (realmRoles ?? []).ToList() },
        };

        return new JsonWebTokenHandler().CreateToken(
            new SecurityTokenDescriptor
            {
                Issuer = issuer ?? Issuer,
                IssuedAt = issuedAt,
                NotBefore = issuedAt,
                Expires = expires,
                Claims = claims,
                SigningCredentials = new SigningCredentials(
                    signedByIssuer ? SigningKey : ForeignKey,
                    SecurityAlgorithms.RsaSha256
                ),
            }
        );
    }

    private static Dictionary<string, string> PublicJwk(RsaSecurityKey key)
    {
        var parameters = key.Rsa.ExportParameters(includePrivateParameters: false);
        return new Dictionary<string, string>
        {
            ["kid"] = key.KeyId,
            ["kty"] = "RSA",
            ["alg"] = SecurityAlgorithms.RsaSha256,
            ["use"] = "sig",
            ["n"] = Base64UrlEncoder.Encode(parameters.Modulus),
            ["e"] = Base64UrlEncoder.Encode(parameters.Exponent),
        };
    }

    private static X509Certificate2 CreateCertificate()
    {
        var request = new CertificateRequest(
            "CN=intercom-server-tests-issuer",
            RSA.Create(2048),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        await ((IAsyncDisposable)_host).DisposeAsync();
    }
}
