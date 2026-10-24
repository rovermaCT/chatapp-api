using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace ChatApp.Api.Extensions;

public static class SwaggerSetup
{
    public const string BearerScheme = "Bearer";
    public const string PasswordScheme = "OAuth2Password";

    public static IServiceCollection AddChatSwagger(this IServiceCollection services)
    {
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo { Title = "ChatApp API", Version = "v1" });

            // Paste an access_token from /api/auth/login or /api/auth/register.
            options.AddSecurityDefinition(BearerScheme, new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Paste the access_token value (without the 'Bearer ' prefix)."
            });

            // Or let Swagger UI log in for you through the OAuth 2.0 password grant on /connect/token.
            options.AddSecurityDefinition(PasswordScheme, new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.OAuth2,
                Description = "Log in with a registered user name/email and password.",
                Flows = new OpenApiOAuthFlows
                {
                    Password = new OpenApiOAuthFlow
                    {
                        TokenUrl = new Uri("/connect/token", UriKind.Relative),
                        Scopes = new Dictionary<string, string>()
                    }
                }
            });

            options.OperationFilter<AuthorizeOperationFilter>();
        });

        return services;
    }

    public static WebApplication UseChatSwagger(this WebApplication app)
    {
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "ChatApp API v1");
            options.EnablePersistAuthorization();
        });
        return app;
    }

    /// <summary>Adds the security requirement (lock icon) only to endpoints that actually require authorization.</summary>
    private sealed class AuthorizeOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
            if (!metadata.OfType<IAuthorizeData>().Any() || metadata.OfType<IAllowAnonymous>().Any())
            {
                return;
            }

            // Two separate requirements mean "either scheme is accepted".
            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(BearerScheme, context.Document)] = []
            });
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(PasswordScheme, context.Document)] = []
            });

            operation.Responses ??= new OpenApiResponses();
            operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Missing or invalid access token" });
        }
    }
}
