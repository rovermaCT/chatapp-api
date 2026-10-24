namespace ChatApp.Api.Options;

public sealed class ExternalAuthOptions
{
    public const string SectionName = "ExternalAuth";

    /// <summary>OpenID Connect providers keyed by scheme name (e.g. "Google", "Microsoft").</summary>
    public Dictionary<string, OidcProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Client redirect URIs that may receive an authorization code after external login.</summary>
    public List<string> AllowedRedirectUris { get; set; } = [];

    public int AuthorizationCodeLifetimeSeconds { get; set; } = 60;

    /// <summary>Providers with a client id; unconfigured entries are skipped rather than failing startup.</summary>
    public IEnumerable<KeyValuePair<string, OidcProviderOptions>> ConfiguredProviders() =>
        Providers.Where(p => !string.IsNullOrWhiteSpace(p.Value.ClientId) && !string.IsNullOrWhiteSpace(p.Value.Authority));
}

public sealed class OidcProviderOptions
{
    public string? DisplayName { get; set; }

    public string Authority { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string? ClientSecret { get; set; }

    public List<string> Scopes { get; set; } = ["openid", "profile", "email"];
}
