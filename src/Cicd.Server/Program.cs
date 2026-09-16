using Cicd.Core.Builds;
using Cicd.Core.Persistence;
using Cicd.Core.Plugins;
using Cicd.Core.Services;
using Cicd.Data;
using Cicd.Plugins.Sdk;
using Cicd.Server.Api;
using Cicd.Server.Components;
using Cicd.Server.Hubs;
using Cicd.Server.Realtime;
using Cicd.Server.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Cicd")
    ?? throw new InvalidOperationException("ConnectionStrings:Cicd is not configured.");

using (var startupLoggers = LoggerFactory.Create(l => l.AddSimpleConsole()))
{
    builder.Services.LoadPlugins(builder.Configuration, PluginSide.Server, startupLoggers.CreateLogger("Plugins"));
}

builder.Services.AddCicdPostgres(connectionString);
builder.Services.AddCicdCore(builder.Configuration);
builder.Services.AddCicdSecurity(builder.Configuration);

builder.Services.AddSingleton<BuildEventStream>();
builder.Services.AddSingleton<IBuildEventPublisher, SignalRBuildEventPublisher>();
builder.Services.AddSingleton<IAgentChannel, SignalRAgentChannel>();

builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 4 * 1024 * 1024)
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddOpenApi("v1");
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("postgres");

var app = builder.Build();

var serverOptions = app.Services.GetRequiredService<IOptions<CicdServerOptions>>().Value;
if (serverOptions.MigrateOnStartup)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CicdDbContext>();
    app.Logger.LogInformation("Applying database migrations");
    await db.Database.MigrateAsync();
}

if (string.IsNullOrEmpty(builder.Configuration["Agents:AuthToken"]) || builder.Configuration["Agents:AuthToken"] == "change-me")
{
    app.Logger.LogWarning("Agents:AuthToken is unset or still the default. Set a real secret before exposing this server.");
}
if (string.IsNullOrEmpty(builder.Configuration["Security:ApiToken"]))
{
    app.Logger.LogWarning("Security:ApiToken is empty: the REST API and UI are unauthenticated.");
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapOpenApi();
app.MapScalarApiReference("/api/docs", o => o.WithTitle("CICD API").WithOpenApiRoutePattern("/openapi/{documentName}.json"));
app.MapHealthChecks("/health");
app.MapCicdApi();
app.MapHub<AgentHub>("/hubs/agents");
app.MapHub<BuildHub>("/hubs/builds");
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
