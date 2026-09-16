using Cicd.Agent;
using Cicd.Core.Plugins;
using Cicd.Plugins.Sdk;
using Microsoft.Extensions.Logging.Console;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; o.ColorBehavior = LoggerColorBehavior.Disabled; });

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
var agentOptions = builder.Configuration.GetSection(AgentOptions.SectionName).Get<AgentOptions>() ?? new AgentOptions();

using var startupLoggerFactory = LoggerFactory.Create(l => l.AddSimpleConsole());
builder.Services.LoadPlugins(builder.Configuration, PluginSide.Agent, startupLoggerFactory.CreateLogger("Plugins"));

builder.Services.AddHttpClient(ArtifactUploader.HttpClientName, client =>
{
    client.BaseAddress = new Uri(agentOptions.ServerUrl.TrimEnd('/') + "/");
    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", agentOptions.AuthToken);
    client.Timeout = TimeSpan.FromMinutes(10);
});
builder.Services.AddSingleton<CapabilityCollector>();
builder.Services.AddSingleton<ArtifactUploader>();
builder.Services.AddSingleton<BuildExecutor>();
builder.Services.AddHostedService<AgentWorker>();

var host = builder.Build();
await host.RunAsync();
