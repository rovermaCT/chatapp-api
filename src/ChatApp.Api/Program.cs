using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatApp.Api.Data;
using ChatApp.Api.Extensions;
using ChatApp.Api.Infrastructure;
using ChatApp.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Serilog;

// Bootstrap logger captures startup failures before configuration is loaded; replaced once the host builds.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddSerilog((services, configuration) => configuration
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services));

    builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddChatSwagger();
    builder.Services.AddOpenApi();
    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton(TimeProvider.System);

    builder.Services.AddDbContext<ChatDbContext>(options =>
        options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

    builder.Services.AddChatIdentity();
    builder.Services.AddChatAuthentication(builder.Configuration);
    builder.Services.AddChatRateLimiting();

    builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
        .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
        .AllowAnyHeader()
        .AllowAnyMethod()));

    builder.Services.AddScoped<TokenService>();
    builder.Services.AddScoped<AuthService>();
    builder.Services.AddScoped<FriendService>();

    var app = builder.Build();

    app.Logger.LogInformation("Starting ChatApp.Api in {Environment}", app.Environment.EnvironmentName);

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.UseChatSwagger();

        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ChatDbContext>().Database.MigrateAsync();
        app.Logger.LogInformation("Database migrations applied");
    }

    // First, so the single per-request event covers everything below, including handled exceptions.
    app.UseSerilogRequestLogging(options => options.EnrichDiagnosticContext = (diagnostics, http) =>
    {
        diagnostics.Set("ClientIp", http.Connection.RemoteIpAddress?.ToString());
        diagnostics.Set("UserAgent", http.Request.Headers.UserAgent.ToString());
        diagnostics.Set("TraceId", http.TraceIdentifier);
        if (http.User.FindFirstValue(JwtRegisteredClaimNames.Sub) is { } userId)
        {
            diagnostics.Set("UserId", userId);
        }
    });
    app.UseExceptionHandler();
    app.UseStatusCodePages();
    app.UseHttpsRedirection();
    app.UseCors();
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "ChatApp.Api terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
