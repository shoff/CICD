using Cicd.Core.Builds;
using Cicd.Core.Plugins;
using Cicd.Core.Services;
using Cicd.Core.Settings;
using Cicd.Data;
using Cicd.Plugins.Sdk;
using Cicd.Server.Api;
using Cicd.Server.Components;
using Cicd.Server.Hubs;
using Cicd.Server.Realtime;
using Cicd.Server.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Cicd")
    ?? throw new InvalidOperationException("ConnectionStrings:Cicd is not configured.");
var dataDirectory = Path.GetFullPath(builder.Configuration["Server:DataDirectory"] ?? "data");
var keyDirectory = new DirectoryInfo(Path.Combine(dataDirectory, "keys"));
keyDirectory.Create();
var secretProtector = new DataProtectionSecretProtector(
    DataProtectionProvider.Create(keyDirectory, o => o.SetApplicationName("cicd")));

DatabaseSettingsConfigurationSource settingsSource;
using (var startupLoggers = LoggerFactory.Create(l => l.AddSimpleConsole()))
{
    var startupLogger = startupLoggers.CreateLogger("Startup");
    try
    {
        if (builder.Configuration.GetValue("Server:MigrateOnStartup", true))
        {
            startupLogger.LogInformation("Applying database migrations");
            await DatabaseStartup.MigrateAsync(connectionString);
        }
        var seeded = await DatabaseStartup.SeedSettingsAsync(connectionString, builder.Configuration, secretProtector);
        if (seeded > 0)
        {
            startupLogger.LogInformation("Seeded {Count} settings from configuration", seeded);
        }
        var encrypted = await DatabaseStartup.EncryptLegacyVcsRootsAsync(connectionString, secretProtector);
        if (encrypted > 0)
        {
            startupLogger.LogInformation("Encrypted {Count} VCS root credential sets", encrypted);
        }
        foreach (var name in await DatabaseStartup.UnreadableVcsRootsAsync(connectionString, secretProtector))
        {
            startupLogger.LogError("VCS root {Name} has credentials that cannot be decrypted; restore the key ring or recreate the root", name);
        }
        // The child keys each list has in appsettings and the environment. The providers are captured before the
        // database source is added, so the answer never includes the database's own entries, and they are asked on
        // every load, so an entry added to appsettings.json while running is shadowed from the next save onwards.
        var lowerLayers = ((IConfigurationRoot)builder.Configuration).Providers.ToList();
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> ShadowedListKeys() => SettingsCatalog.All
            .Where(definition => definition.Kind == SettingKind.List)
            .ToDictionary(
                definition => definition.Key,
                definition => (IReadOnlyCollection<string>)lowerLayers
                    .SelectMany(provider => provider.GetChildKeys([], definition.Key))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);
        settingsSource = new DatabaseSettingsConfigurationSource(
            connectionString, secretProtector, ShadowedListKeys, startupLoggers.CreateLogger("Settings"));
        // Inside the try: adding the source triggers the provider's first Load(), which reads the table.
        builder.Configuration.Sources.Add(settingsSource);
    }
    catch (Exception ex)
    {
        startupLogger.LogCritical(ex, "Cannot reach the database at startup ({Message})", ex.Message);
        throw;
    }
    builder.Services.AddSingleton<ISecretProtector>(secretProtector);
    builder.Services.AddSingleton<ISettingsReloader>(settingsSource.Provider);
    builder.Services.AddDataProtection().PersistKeysToFileSystem(keyDirectory).SetApplicationName("cicd");

    builder.Services.LoadPlugins(builder.Configuration, PluginSide.Server, startupLoggers.CreateLogger("Plugins"));
}

builder.Services.AddCicdPostgres(connectionString);
builder.Services.AddCicdCore(builder.Configuration);
builder.Services.AddCicdSecurity(builder.Configuration, builder.Environment.IsDevelopment());

builder.Services.AddSingleton<BuildEventStream>();
builder.Services.AddSingleton<IBuildEventPublisher, SignalRBuildEventPublisher>();
builder.Services.AddSingleton<IAgentChannel, SignalRAgentChannel>();

builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 4 * 1024 * 1024)
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddOpenApi("v1");
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("postgres");

var app = builder.Build();
settingsSource.Provider.Logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Settings");

if (string.IsNullOrEmpty(builder.Configuration["Agents:AuthToken"]) || builder.Configuration["Agents:AuthToken"] == "change-me")
{
    app.Logger.LogWarning("Agents:AuthToken is unset or still the default. Set a real secret before exposing this server.");
}
var identityOptions = app.Services.GetRequiredService<IOptions<IdentityProviderOptions>>().Value;
if (!identityOptions.IsConfigured)
{
    app.Logger.LogWarning(string.IsNullOrEmpty(builder.Configuration["Security:ApiToken"])
        ? "IdentityProvider:Authority is not configured and Security:ApiToken is empty: the UI and API are open."
        : "IdentityProvider:Authority is not configured: the UI and API accept only the static Security:ApiToken.");
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
app.MapCicdAuth();
app.MapHub<AgentHub>("/hubs/agents");
app.MapHub<BuildHub>("/hubs/builds").RequireAuthorization(Policies.Viewer);
app.MapRazorComponents<App>().AddInteractiveServerRenderMode().RequireAuthorization(Policies.Viewer);

app.Run();

public partial class Program;
