using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using LawWatcher.BuildingBlocks.Configuration;
using LawWatcher.BuildingBlocks.Health;
using LawWatcher.Portal.Services;
using LawWatcher.Portal.Components;

var builder = WebApplication.CreateBuilder(args);

var dataProtectionKeysDirectory = new DirectoryInfo(Path.Combine(
    builder.Environment.ContentRootPath,
    "artifacts",
    "dataprotection",
    builder.Environment.ApplicationName));
dataProtectionKeysDirectory.Create();

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddDebug();
}

builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(dataProtectionKeysDirectory);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.Configure<PortalApiOptions>(builder.Configuration.GetSection("LawWatcher:PortalApi"));
builder.Services.Configure<HostHealthOptions>(builder.Configuration.GetSection("LawWatcher:Health"));
var hostHealthOptions = builder.Configuration.GetSection("LawWatcher:Health").Get<HostHealthOptions>() ?? new HostHealthOptions();
var readinessState = new HostReadinessState();
builder.Services.AddSingleton(readinessState);
builder.Services.AddSingleton<HostReadinessHealthCheck>();
var healthChecks = builder.Services.AddHealthChecks();
healthChecks.AddCheck("self", () => HealthCheckResult.Healthy("Portal host is running."), tags: [LawWatcherHealthTags.Live]);
healthChecks.AddCheck<HostReadinessHealthCheck>("startup", tags: [LawWatcherHealthTags.Ready]);
builder.Services.AddHttpClient<LawWatcherPortalApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<PortalApiOptions>>().Value;
    client.BaseAddress = options.GetBaseUri();
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddScoped<LawWatcherPortalAdminClient>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
if (ShouldUseHttpsRedirection(builder.Configuration))
{
    app.UseHttpsRedirection();
}

app.UseAntiforgery();
app.MapGet(hostHealthOptions.LivePath, async (HttpContext context, HealthCheckService healthCheckService) =>
{
    await LawWatcherHealthResponseWriter.WriteAsync(
        context,
        healthCheckService,
        LawWatcherHealthTags.Live,
        context.RequestAborted);
}).AllowAnonymous();
app.MapGet(hostHealthOptions.ReadyPath, async (HttpContext context, HealthCheckService healthCheckService) =>
{
    await LawWatcherHealthResponseWriter.WriteAsync(
        context,
        healthCheckService,
        LawWatcherHealthTags.Ready,
        context.RequestAborted);
}).AllowAnonymous();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.Lifetime.ApplicationStarted.Register(readinessState.MarkReady);

app.Run();

static bool ShouldUseHttpsRedirection(IConfiguration configuration)
{
    var configuredUrls = configuration["ASPNETCORE_URLS"] ?? configuration["urls"] ?? string.Empty;
    return configuredUrls
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
}
