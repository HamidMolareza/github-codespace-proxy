using System.Text.Json.Serialization;
using GhProxy.Api.Data;
using GhProxy.Api.Endpoints;
using GhProxy.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var dataPath = Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataPath);
Directory.CreateDirectory(Path.Combine(dataPath, "keys"));

builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.Configure<ProxyRuntimeOptions>(builder.Configuration.GetSection("ProxyRuntime"));
builder.Services.Configure<ObservabilityOptions>(builder.Configuration.GetSection("Observability"));
builder.Services.Configure<GitHubOptions>(builder.Configuration.GetSection("GitHub"));
builder.Services.Configure<LocalProxyOptions>(builder.Configuration.GetSection("LocalProxy"));
builder.Services.AddDataProtection()
    .SetApplicationName("GhProxy")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "keys")));
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=data/gh-proxy.db"));
builder.Services.AddHttpClient<IGitHubApiClient, GitHubApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<GitHubOptions>>().Value;
    client.BaseAddress = new Uri(options.ApiBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.RequestTimeoutSeconds, 5, 120));
});
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<ICorrelationContext, CorrelationContext>();
builder.Services.AddSingleton<ISensitiveDataRedactor, SensitiveDataRedactor>();
builder.Services.AddSingleton<IOperationalEventSink, OperationalEventSink>();
builder.Services.AddSingleton<ICommandRunner, ProcessCommandRunner>();
builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
builder.Services.AddScoped<DatabaseSchemaInitializer>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<NodeConfigRenderer>();
builder.Services.AddScoped<VpsRuntimeService>();
builder.Services.AddScoped<TunnelService>();
builder.Services.AddScoped<GitHubCodespaceService>();
builder.Services.AddSingleton<LocalProxyRuntimeService>();
builder.Services.AddHostedService<IdleShutdownService>();
builder.Services.AddHostedService<LocalProxyIdleShutdownService>();
builder.Services.AddHostedService<GitHubCodespaceMaintenanceService>();
builder.Services.AddHostedService<ObservabilityRetentionService>();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins("http://127.0.0.1:5173", "http://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod());
});

var app = builder.Build();

Directory.CreateDirectory(dataPath);
using (var scope = app.Services.CreateScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<DatabaseSchemaInitializer>();
    await initializer.InitializeAsync(CancellationToken.None);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseMiddleware<CorrelationMiddleware>();
app.UseMiddleware<ApiExceptionMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseCors();
app.MapGet("/api/health", (ICorrelationContext correlation) =>
    Results.Ok(new { status = "ok", at = DateTimeOffset.UtcNow, correlationId = correlation.CorrelationId }));
app.MapNodeEndpoints();
app.MapSessionEndpoints();
app.MapLocalProxyEndpoints();
app.MapGitHubEndpoints();
app.MapObservabilityEndpoints();

app.Run();

public partial class Program;
