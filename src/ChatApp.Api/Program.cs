using System.Text.Json;
using System.Text.Json.Serialization;
using ChatApp.Api.Data;
using ChatApp.Api.Extensions;
using ChatApp.Api.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddProblemDetails();
builder.Services.AddChatSwagger();
builder.Services.AddOpenApi();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddDbContext<ChatDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("Default"),
        sqlServer => sqlServer.EnableRetryOnFailure()));

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

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseChatSwagger();

    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<ChatDbContext>().Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseHttpsRedirection();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
