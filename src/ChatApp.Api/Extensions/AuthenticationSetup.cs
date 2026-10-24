using System.Threading.RateLimiting;
using ChatApp.Api.Controllers;
using ChatApp.Api.Data;
using ChatApp.Api.Entities;
using ChatApp.Api.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace ChatApp.Api.Extensions;

public static class RateLimitPolicies
{
    public const string Auth = "auth";
}

public static class AuthenticationSetup
{
    public static IServiceCollection AddChatIdentity(this IServiceCollection services)
    {
        // AddIdentityCore (not AddIdentity) so Identity doesn't make cookies the default scheme; this API speaks JWT.
        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 8;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<ChatDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        return services;
    }

    public static IServiceCollection AddChatAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>()
            .BindConfiguration(JwtOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<ExternalAuthOptions>()
            .BindConfiguration(ExternalAuthOptions.SectionName);

        var authentication = services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer()
            // Holds the OIDC provider's identity for the few seconds between its callback and ours.
            .AddCookie(IdentityConstants.ExternalScheme, options =>
            {
                options.Cookie.Name = "chatapp.external";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
            });

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((options, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = jwt.GetSigningKey(),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = JwtRegisteredClaimNames.UniqueName,
                    RoleClaimType = Services.AppClaimTypes.Role
                };
            });

        var externalAuth = configuration.GetSection(ExternalAuthOptions.SectionName).Get<ExternalAuthOptions>() ?? new();
        foreach (var (name, provider) in externalAuth.ConfiguredProviders())
        {
            authentication.AddOpenIdConnect(name, provider.DisplayName ?? name, options =>
            {
                options.SignInScheme = IdentityConstants.ExternalScheme;
                options.Authority = provider.Authority;
                options.ClientId = provider.ClientId;
                options.ClientSecret = provider.ClientSecret;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.UsePkce = true;
                options.CallbackPath = $"/signin-{name.ToLowerInvariant()}";
                options.MapInboundClaims = false;
                options.GetClaimsFromUserInfoEndpoint = true;
                options.SaveTokens = false;
                options.Scope.Clear();
                foreach (var scope in provider.Scopes)
                {
                    options.Scope.Add(scope);
                }
                options.ClaimActions.MapUniqueJsonKey("email_verified", "email_verified");
                options.TokenValidationParameters.NameClaimType = "name";
                options.Events.OnRemoteFailure = HandleRemoteFailure;
            });
        }

        services.AddAuthorization();
        return services;
    }

    public static IServiceCollection AddChatRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(RateLimitPolicies.Auth, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
        });
        return services;
    }

    /// <summary>The user cancelled at the provider, or the provider returned an error.</summary>
    private static Task HandleRemoteFailure(RemoteFailureContext context)
    {
        context.HandleResponse();
        context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(AuthenticationSetup))
            .LogWarning(context.Failure, "External login with {Scheme} failed", context.Scheme.Name);

        const string description = "External login was cancelled or failed.";
        var properties = context.Properties;
        if (properties?.GetString(ExternalAuthItems.RedirectUri) is { } redirectUri)
        {
            context.Response.Redirect(QueryHelpers.AddQueryString(redirectUri, new Dictionary<string, string?>
            {
                ["error"] = "access_denied",
                ["error_description"] = description,
                ["state"] = properties.GetString(ExternalAuthItems.State)
            }));
            return Task.CompletedTask;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "External login failed",
            Detail = description
        });
    }
}
