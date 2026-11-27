namespace IntercomServer;

/// <summary>
/// Sign-in: the Keycloak realm whose bearer tokens the API accepts, and the client they are for.
/// </summary>
internal class SignInConfiguration
{
    /// <summary>
    /// The realm's issuer URL (<c>KEYCLOAK_ISSUER</c>), required, over HTTPS. A token must carry
    /// it as its <c>iss</c>, and the server reads the realm's signing keys from its discovery
    /// document.
    /// </summary>
    public string? Issuer { get; init; }

    /// <summary>
    /// The client the tokens are for (<c>KEYCLOAK_AUDIENCE</c>): a token must name it in its
    /// <c>aud</c>, and its client roles are the server's roles.
    /// </summary>
    public string Audience { get; init; } = "intercom-server";

    /// <summary>
    /// Validates the configuration at startup, throwing on a missing issuer or one that is not an
    /// HTTPS URL.
    /// </summary>
    public void Validate()
    {
        if (!Uri.TryCreate(Issuer, UriKind.Absolute, out var issuer) || issuer.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(
                "KEYCLOAK_ISSUER is required: the HTTPS URL of the Keycloak realm whose tokens the API accepts."
            );
    }
}
