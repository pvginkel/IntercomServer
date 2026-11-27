using System.Net;
using IntercomServer.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IntercomServer.Tests;

/// <summary>
/// Every <c>/api</c> call takes a Keycloak bearer token, validated against the realm and client
/// the server's settings name, and a route asks for a client role of that client: a token
/// without one answers 401, one without the role 403. Shown on <c>POST /api/app-releases</c>,
/// which asks for <c>pipeline</c>, with a release that a passing call would publish.
/// </summary>
public sealed class SignInTests
{
    private static readonly Dictionary<string, string[]> Pipeline = new()
    {
        [TestIssuer.ServerClient] = ["pipeline"],
    };

    [Fact]
    public async Task A_call_without_a_valid_token_answers_401()
    {
        await using var harness = await ServerHarness.StartAsync();
        var issuer = harness.Issuer;
        var tokens = new Dictionary<string, string?>
        {
            ["none"] = null,
            ["malformed"] = "not-a-token",
            ["expired"] = issuer.CreateToken(clientRoles: Pipeline, expiresIn: TimeSpan.FromMinutes(-10)),
            ["from another issuer"] = issuer.CreateToken(
                clientRoles: Pipeline,
                issuer: "https://127.0.0.1:1/realms/another"
            ),
            ["for another audience"] = issuer.CreateToken(clientRoles: Pipeline, audiences: ["another-client"]),
            ["signed by another key"] = issuer.CreateToken(clientRoles: Pipeline, signedByIssuer: false),
        };

        var answers = new Dictionary<string, HttpStatusCode>();
        foreach (var (kind, token) in tokens)
        {
            using var response = await harness.PublishAsync(token, "1", AppReleases.Apk());
            answers[kind] = response.StatusCode;
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        }

        Assert.Equal(tokens.Keys.ToDictionary(kind => kind, _ => HttpStatusCode.Unauthorized), answers);
        Assert.Empty(harness.UpdatesFiles());
    }

    [Fact]
    public async Task A_valid_token_without_the_routes_role_on_the_servers_client_answers_403()
    {
        await using var harness = await ServerHarness.StartAsync();
        var issuer = harness.Issuer;
        var tokens = new Dictionary<string, string>
        {
            ["no role"] = issuer.CreateToken(),
            ["another role"] = issuer.TokenWithRoles("user"),
            ["the role on another client"] = issuer.CreateToken(
                clientRoles: new Dictionary<string, string[]>
                {
                    [TestIssuer.ServerClient] = ["user"],
                    ["another-client"] = ["pipeline"],
                }
            ),
            ["the role as a realm role"] = issuer.CreateToken(realmRoles: ["pipeline"]),
        };

        var answers = new Dictionary<string, HttpStatusCode>();
        foreach (var (kind, token) in tokens)
        {
            using var response = await harness.PublishAsync(token, "1", AppReleases.Apk());
            answers[kind] = response.StatusCode;
        }

        Assert.Equal(tokens.Keys.ToDictionary(kind => kind, _ => HttpStatusCode.Forbidden), answers);
        Assert.Empty(harness.UpdatesFiles());
    }

    [Fact]
    public async Task A_token_for_several_audiences_with_the_role_on_the_servers_client_passes()
    {
        await using var harness = await ServerHarness.StartAsync();
        var token = harness.Issuer.CreateToken(
            clientRoles: new Dictionary<string, string[]>
            {
                [TestIssuer.ServerClient] = ["user", "pipeline"],
                ["account"] = ["view-profile"],
            },
            realmRoles: ["offline_access"],
            audiences: [TestIssuer.ServerClient, "account"]
        );

        using var response = await harness.PublishAsync(token, "1", AppReleases.Apk());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task The_audience_and_its_client_roles_are_the_settings()
    {
        await using var harness = await ServerHarness.StartAsync(
            new Dictionary<string, string?> { ["KEYCLOAK_AUDIENCE"] = "another-client" }
        );
        var issuer = harness.Issuer;

        using var forTheDefault = await harness.PublishAsync(issuer.TokenWithRoles("pipeline"), "1", AppReleases.Apk());
        using var forTheSetting = await harness.PublishAsync(
            issuer.CreateToken(
                clientRoles: new Dictionary<string, string[]> { ["another-client"] = ["pipeline"] },
                audiences: ["another-client"]
            ),
            "1",
            AppReleases.Apk()
        );

        Assert.Equal(HttpStatusCode.Unauthorized, forTheDefault.StatusCode);
        Assert.Equal(HttpStatusCode.Created, forTheSetting.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("realms/homelab")]
    [InlineData("http://127.0.0.1/realms/homelab")]
    public async Task The_server_refuses_to_start_without_an_https_issuer(string? issuer)
    {
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ServerHarness.StartAsync(new Dictionary<string, string?> { ["KEYCLOAK_ISSUER"] = issuer })
        );

        Assert.StartsWith("KEYCLOAK_ISSUER is required", refusal.Message);
    }

    [Fact]
    public async Task The_server_starts_without_data_protections_hosted_service()
    {
        await using var harness = await ServerHarness.StartAsync();

        // Its key ring would warn at every start that its key is stored unencrypted.
        Assert.DoesNotContain(
            harness.Services.GetServices<IHostedService>(),
            service => service.GetType().Namespace?.StartsWith("Microsoft.AspNetCore.DataProtection") == true
        );
    }

    [Fact]
    public async Task The_update_channel_takes_no_token_and_ignores_an_invalid_one()
    {
        await using var harness = await ServerHarness.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/updates?action=version");
        request.Headers.Authorization = new("Bearer", "not-a-token");

        using var withoutToken = await harness.GetUpdatesAsync("?action=version");
        using var withInvalidToken = await harness.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, withoutToken.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withInvalidToken.StatusCode);
    }
}
