using GhProxy.Api.Data;
using GhProxy.Api.Endpoints;
using GhProxy.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.Configure<ProxyRuntimeOptions>(builder.Configuration.GetSection("ProxyRuntime"));
builder.Services.AddDataProtection().SetApplicationName("GhProxy");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=data/gh-proxy.db"));
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<ICommandRunner, ProcessCommandRunner>();
builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<NodeConfigRenderer>();
builder.Services.AddScoped<VpsRuntimeService>();
builder.Services.AddScoped<TunnelService>();
builder.Services.AddHostedService<IdleShutdownService>();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins("http://127.0.0.1:5173", "http://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod());
});

var app = builder.Build();

Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "data"));
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", at = DateTimeOffset.UtcNow }));
app.MapNodeEndpoints();
app.MapSessionEndpoints();

app.Run();

public partial class Program;
